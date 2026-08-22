"""Validate release metadata against pinned upstream schemas without network access."""

import argparse
import copy
import hashlib
import json
from pathlib import Path

import jsonschema
import yaml


QUALITY = Path(__file__).resolve().parents[1] / ".github" / "quality"
CONTEXT = "https://spdx.org/rdf/3.0.1/spdx-context.jsonld"
WINGET_VERSION = "1.28.0"


def unique_mapping(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate metadata key: {key}")
        result[key] = value
    return result


def resource(name):
    lock = json.loads((QUALITY / "schemas-lock.json").read_text(encoding="utf-8"))
    path = (QUALITY / name).resolve()
    if not path.is_relative_to(QUALITY.resolve()):
        raise ValueError("Schema resource escaped the quality directory")
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != lock[name]["sha256"]:
        raise ValueError(f"Pinned schema resource changed: {name}")
    return data.decode("utf-8")


def validate_sbom(document):
    from pyld import jsonld
    from pyshacl import validate
    from rdflib import Dataset, Graph

    schema = json.loads(resource("spdx-3.0.1/spdx-json-schema.json"))
    if document.get("@context") != CONTEXT or set(document) != {"@context", "@graph"}:
        raise ValueError("Expected an SPDX 3.0.1 JSON-LD graph")
    if not isinstance(document["@graph"], list) or not document["@graph"]:
        raise ValueError("SPDX graph must contain elements")
    jsonschema.Draft202012Validator(schema, format_checker=jsonschema.FormatChecker()).validate(document)
    context = json.loads(resource("spdx-3.0.1/spdx-context.jsonld"))

    def load_context(url, options=None):
        if url != CONTEXT:
            raise ValueError(f"External JSON-LD loading is disabled: {url}")
        return {"contextUrl": None, "documentUrl": url, "document": context}

    nquads = jsonld.to_rdf(document, options={"documentLoader": load_context, "format": "application/n-quads"})
    data = Dataset(default_union=True)
    data.parse(data=nquads, format="nquads")
    ontology = Graph().parse(data=resource("spdx-3.0.1/spdx-model.ttl"), format="turtle")
    # Materialized RDFS types would instantiate abstract classes forbidden by SPDX's shapes.
    conforms, _, report = validate(data, shacl_graph=ontology, ont_graph=ontology,
                                   inference="none", advanced=True, do_owl_imports=False, js=False)
    if not conforms:
        raise ValueError(f"SPDX semantic validation failed:\n{report}")


class UniqueYamlLoader(yaml.SafeLoader):
    pass


def yaml_mapping(loader, node):
    loader.flatten_mapping(node)
    return unique_mapping((loader.construct_object(key), loader.construct_object(value)) for key, value in node.value)


UniqueYamlLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, yaml_mapping)


def validate_winget(documents):
    types = {"version": "version", "installer": "installer", "defaultLocale": "defaultLocale"}
    if len(documents) != 3 or {item.get("ManifestType") for item in documents} != set(types):
        raise ValueError("Expected exactly one version, installer and defaultLocale manifest")
    identities = set()
    for document in documents:
        if document.get("ManifestVersion") != WINGET_VERSION:
            raise ValueError(f"Expected WinGet schema {WINGET_VERSION}")
        schema = json.loads(resource(f"winget-{WINGET_VERSION}/{types[document['ManifestType']]}.json"))
        jsonschema.Draft7Validator(schema, format_checker=jsonschema.FormatChecker()).validate(document)
        identities.add((document["PackageIdentifier"], document["PackageVersion"], document["ManifestVersion"]))
    if len(identities) != 1:
        raise ValueError("WinGet manifest identities differ")
    installer = next(item for item in documents if item["ManifestType"] == "installer")
    architectures = [item["Architecture"] for item in installer["Installers"]]
    if len(architectures) != 3 or set(architectures) != {"x86", "x64", "arm64"}:
        raise ValueError("Expected exactly the x86, x64 and arm64 installers")


def self_test(sbom, winget):
    cases = []
    if sbom:
        changed = copy.deepcopy(sbom)
        changed["@context"] = "https://invalid.example/context"
        cases.append(("remote-context", lambda: validate_sbom(changed)))
        invalid = copy.deepcopy(sbom)
        package = next(item for item in invalid["@graph"] if item["type"] == "software_Package")
        package["software_packageVersion"] = 123
        cases.append(("invalid-package-type", lambda: validate_sbom(invalid)))
        missing = copy.deepcopy(sbom)
        creation = next(item for item in missing["@graph"] if item["type"] == "CreationInfo")
        creation["createdBy"] = ["https://invalid.example/missing-agent"]
        cases.append(("unresolved-agent", lambda: validate_sbom(missing)))
    if winget:
        invalid_winget = copy.deepcopy(winget)
        locale = next(item for item in invalid_winget if item["ManifestType"] == "defaultLocale")
        del locale["ShortDescription"]
        cases.append(("required-description", lambda: validate_winget(invalid_winget)))
        duplicates = copy.deepcopy(winget)
        installers = next(item for item in duplicates if item["ManifestType"] == "installer")
        installers["Installers"].append(copy.deepcopy(installers["Installers"][0]))
        cases.append(("duplicate-architecture", lambda: validate_winget(duplicates)))
        old_schema = copy.deepcopy(winget)
        for document in old_schema:
            document["ManifestVersion"] = "1.12.0"
        cases.append(("obsolete-winget-schema", lambda: validate_winget(old_schema)))
    for name, action in cases:
        try:
            action()
        except (ValueError, jsonschema.ValidationError):
            print(f"Schema rejection passed: {name}")
        else:
            raise ValueError(f"Invalid schema fixture accepted: {name}")
    for parser, text in ((lambda value: json.loads(value, object_pairs_hook=unique_mapping), '{"a":1,"a":2}'),
                         (lambda value: yaml.load(value, Loader=UniqueYamlLoader), "a: 1\na: 2\n")):
        try:
            parser(text)
        except ValueError:
            continue
        raise ValueError("Duplicate metadata keys were accepted")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sbom", type=Path)
    parser.add_argument("--winget", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if not args.sbom and not args.winget:
        parser.error("Specify --sbom and/or --winget")
    sbom = None
    winget = None
    if args.sbom:
        sbom = json.loads(args.sbom.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_mapping)
        validate_sbom(sbom)
        print("SPDX 3.0.1 structural and semantic validation passed")
    if args.winget:
        files = sorted(args.winget.rglob("*.yaml"))
        winget = [yaml.load(path.read_text(encoding="utf-8-sig"), Loader=UniqueYamlLoader) for path in files]
        validate_winget(winget)
        print(f"WinGet {WINGET_VERSION} schema validation passed")
    if args.self_test:
        self_test(sbom, winget)


if __name__ == "__main__":
    main()
