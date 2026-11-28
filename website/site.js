"use strict";

function parseReleaseDownloads() {
  const dataElement = document.getElementById("release-data");
  if (!dataElement || !dataElement.textContent) return null;
  try {
    const parsed = JSON.parse(dataElement.textContent);
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

function hasCompletePackages(entry) {
  if (!entry || typeof entry !== "object") return false;
  return ["installer", "framework", "portable"].every((kind) => {
    const pkg = entry[kind];
    return (
      pkg &&
      typeof pkg.url === "string" &&
      pkg.url.length > 0 &&
      typeof pkg.size === "string" &&
      pkg.size.length > 0
    );
  });
}

const downloads = parseReleaseDownloads();
const architecture = document.getElementById("architecture");
const archButtons = [...document.querySelectorAll("[data-arch]")];

for (const control of document.querySelectorAll("[data-enhanced]")) {
  control.hidden = false;
}

if (downloads) {
  const validArches = archButtons.filter((button) =>
    hasCompletePackages(downloads[button.dataset.arch]),
  );
  if (validArches.length > 0) {
    for (const control of document.querySelectorAll("[data-arch-enhanced]")) {
      control.hidden = false;
    }
    for (const button of archButtons) {
      if (!hasCompletePackages(downloads[button.dataset.arch])) {
        button.hidden = true;
      }
    }
  }
}

function updateDownloads(selectedArch) {
  if (!downloads || !architecture) return;
  const arch = selectedArch ?? architecture.value;
  const selected = downloads[arch];
  if (!hasCompletePackages(selected)) return;

  const label = arch === "arm64" ? "ARM64" : arch;
  const links = [];
  for (const kind of ["installer", "framework", "portable"]) {
    const link = document.getElementById(`${kind}-download`);
    const size = document.getElementById(`${kind}-size`);
    if (!link || !size) return;
    links.push([kind, link, size]);
  }

  architecture.value = arch;
  for (const button of archButtons) {
    button.setAttribute("aria-pressed", String(button.dataset.arch === arch));
  }

  for (const [kind, link, size] of links) {
    link.href = selected[kind].url;
    size.textContent = `${label} · ${selected[kind].size} · ${kind === "installer" ? "MSI" : "ZIP"}`;
  }

  const installer = document.getElementById("installer-download");
  installer.replaceChildren(
    document.createTextNode(`Download ${label} installer `),
  );
  const arrow = document.createElement("span");
  arrow.setAttribute("aria-hidden", "true");
  arrow.textContent = "↓";
  installer.append(arrow);
}

for (const button of archButtons) {
  button.addEventListener("click", () => {
    if (button.getAttribute("aria-pressed") === "true") return;
    updateDownloads(button.dataset.arch);
  });
}

if (architecture) {
  architecture.addEventListener("change", () => updateDownloads());
}
window.addEventListener("pageshow", () => updateDownloads());

const heroThemeButtons = [...document.querySelectorAll("[data-theme]")];

function setHeroPreviewTheme(theme) {
  const normalized = theme === "light" ? "light" : "dark";
  for (const button of heroThemeButtons) {
    button.setAttribute(
      "aria-pressed",
      String(button.dataset.theme === normalized),
    );
  }
  const image = document.getElementById("hero-screenshot");
  if (!image) return;
  image.src = `assets/main_window_${normalized}.png`;
  const tab = normalized === "light" ? "Input" : "Output";
  image.alt = `AudioPilot ${tab} tab showing device selection and switch order`;
  const shell = image.closest(".window-shell");
  if (shell) {
    shell.dataset.previewTheme = normalized;
  }
  const windowTitle = document.getElementById("hero-window-title");
  if (windowTitle) {
    windowTitle.textContent = `AudioPilot — ${tab}`;
  }
  const fullsize = document.getElementById("hero-fullsize");
  if (fullsize) {
    fullsize.href = image.src;
    fullsize.setAttribute(
      "aria-label",
      `View full-size ${tab} screenshot (opens in a new tab)`,
    );
  }
}

for (const button of heroThemeButtons) {
  button.addEventListener("click", () => {
    if (button.getAttribute("aria-pressed") === "true") return;
    setHeroPreviewTheme(button.dataset.theme);
  });
}

const siteThemeToggle = document.getElementById("site-theme-toggle");
const lightPreference = window.matchMedia("(prefers-color-scheme: light)");

function getCurrentSiteTheme() {
  const explicit = document.documentElement.dataset.siteTheme;
  if (explicit === "light" || explicit === "dark") return explicit;
  return lightPreference.matches ? "light" : "dark";
}

function syncThemeChrome(theme) {
  const isLight = theme === "light";
  if (siteThemeToggle) {
    siteThemeToggle.setAttribute("aria-pressed", String(isLight));
  }
  const themeColor = document.querySelector('meta[name="theme-color"]');
  if (themeColor) {
    themeColor.setAttribute("content", isLight ? "#f6f7f9" : "#121315");
  }
}

function applySiteTheme(theme, syncHero = false) {
  document.documentElement.dataset.siteTheme = theme;
  syncThemeChrome(theme);
  if (syncHero) {
    setHeroPreviewTheme(theme);
  }
}

const initialTheme = getCurrentSiteTheme();
syncThemeChrome(initialTheme);
setHeroPreviewTheme(initialTheme);

if (siteThemeToggle) {
  siteThemeToggle.addEventListener("click", () => {
    const next = getCurrentSiteTheme() === "light" ? "dark" : "light";
    applySiteTheme(next, true);
  });
}

if (typeof lightPreference.addEventListener === "function") {
  lightPreference.addEventListener("change", (event) => {
    if (document.documentElement.dataset.siteTheme) return;
    const osTheme = event.matches ? "light" : "dark";
    syncThemeChrome(osTheme);
    setHeroPreviewTheme(osTheme);
  });
}

const views = {
  mixer: {
    label: "Volume Mixer",
    image: "volume_mixer.png",
    height: 880,
    alt: "AudioPilot Volume Mixer showing active devices, application levels and mute controls",
  },
  routines: {
    label: "Routines",
    image: "routines_dashboard.png",
    height: 1180,
    alt: "AudioPilot Routines tab with saved routines and a details pane",
  },
  input: {
    label: "Recording Mixer",
    image: "recording_mixer.png",
    height: 880,
    alt: "AudioPilot Recording Mixer showing the active microphone and recording application controls",
  },
  settings: {
    label: "Settings",
    image: "settings.png",
    height: 1200,
    alt: "AudioPilot Settings showing language, theme, app behavior, and overlay preferences",
  },
};

const viewTabs = [...document.querySelectorAll("[data-view]")];

function activateViewTab(button, moveFocus = false) {
  const view = views[button.dataset.view];
  if (!view) return;
  for (const sibling of viewTabs) {
    const selected = sibling === button;
    sibling.setAttribute("aria-selected", String(selected));
    sibling.tabIndex = selected ? 0 : -1;
  }
  for (const panel of document.querySelectorAll(
    "[data-view-panel], [data-view-caption]",
  )) {
    const active =
      (panel.dataset.viewPanel ?? panel.dataset.viewCaption) ===
      button.dataset.view;
    panel.classList.toggle("is-active", active);
    panel.setAttribute("aria-hidden", String(!active));
    panel.toggleAttribute("inert", !active);
    if (panel.hasAttribute("data-view-panel")) {
      panel.tabIndex = active ? 0 : -1;
    }
  }
  const image = document.getElementById("detail-screenshot");
  if (image) {
    image.src = `assets/${view.image}`;
    image.alt = view.alt;
    image.height = view.height;
  }
  const detailTitle = document.getElementById("detail-window-title");
  if (detailTitle) {
    detailTitle.textContent = `AudioPilot — ${view.label}`;
  }
  const fullsize = document.getElementById("detail-fullsize");
  if (fullsize && image) {
    fullsize.href = image.src;
    fullsize.setAttribute(
      "aria-label",
      `View full-size ${view.label} screenshot (opens in a new tab)`,
    );
  }
  if (moveFocus) button.focus();
}

for (let index = 0; index < viewTabs.length; index++) {
  const button = viewTabs[index];
  button.addEventListener("click", () => {
    if (button.getAttribute("aria-selected") === "true") return;
    activateViewTab(button);
  });
  button.addEventListener("keydown", (event) => {
    let nextIndex = -1;
    if (event.key === "ArrowRight") nextIndex = (index + 1) % viewTabs.length;
    else if (event.key === "ArrowLeft")
      nextIndex = (index - 1 + viewTabs.length) % viewTabs.length;
    else if (event.key === "Home") nextIndex = 0;
    else if (event.key === "End") nextIndex = viewTabs.length - 1;
    if (nextIndex < 0) return;
    event.preventDefault();
    activateViewTab(viewTabs[nextIndex], true);
  });
}

const copyButton = document.getElementById("copy-cli-button");
const copyStatus = document.getElementById("copy-cli-status");
const cliSnippet = document.getElementById("cli-snippet");

if (copyButton && cliSnippet && navigator.clipboard) {
  let resetTimer = 0;
  const resetCopyState = () => {
    copyButton.textContent = "Copy";
    delete copyButton.dataset.copied;
    if (copyStatus) copyStatus.textContent = "";
  };
  copyButton.addEventListener("click", async () => {
    window.clearTimeout(resetTimer);
    try {
      await navigator.clipboard.writeText(cliSnippet.textContent ?? "");
      copyButton.textContent = "Copied";
      copyButton.dataset.copied = "true";
      if (copyStatus) copyStatus.textContent = "CLI examples copied to clipboard.";
    } catch {
      delete copyButton.dataset.copied;
      copyButton.textContent = "Copy failed";
      if (copyStatus) copyStatus.textContent = "Could not copy CLI examples to clipboard.";
    }
    resetTimer = window.setTimeout(resetCopyState, 1800);
  });
} else if (copyButton) {
  copyButton.hidden = true;
}
