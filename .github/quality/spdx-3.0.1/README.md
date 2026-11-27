# SPDX 3.0.1 validation resources

These unmodified official resources support offline release validation. They are build inputs and are not included
in the application downloads. `../schemas-lock.json` records their source URLs and SHA-256 hashes; the validator
checks those hashes before use.

- JSON Schema: <https://spdx.org/schema/3.0.1/spdx-json-schema.json>
- JSON-LD context: <https://spdx.org/rdf/3.0.1/spdx-context.jsonld>
- OWL ontology and SHACL shapes: <https://spdx.org/rdf/3.0.1/spdx-model.ttl>
- Specification: <https://spdx.github.io/spdx-spec/v3.0.1/serializations/>

The SPDX specification and contributors' licensing terms are preserved in `LICENSE`, copied from the upstream
`3.0.1` release. The primary license is Community Specification License 1.0; the file also explains pre-existing
CC-BY-3.0 portions and the license of the upstream license-list utility. AudioPilot's license does not replace them.

Updating resources requires reviewing the corresponding specification version, replacing the resource bytes and
hashes together, and rerunning structural, semantic, and mutation checks. The validator loads only the pinned context,
does not follow ontology imports, and does not materialize abstract RDF types forbidden by the SPDX shapes.
