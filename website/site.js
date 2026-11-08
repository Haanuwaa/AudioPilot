"use strict";

const downloads = JSON.parse(
  document.getElementById("release-data").textContent,
);
const architecture = document.getElementById("architecture");
for (const control of document.querySelectorAll("[data-enhanced]"))
  control.hidden = false;

function updateDownloads() {
  const selected = downloads[architecture.value];
  if (!selected) return;
  const label = architecture.value === "arm64" ? "ARM64" : architecture.value;
  for (const kind of ["installer", "framework", "portable"]) {
    document.getElementById(`${kind}-download`).href = selected[kind].url;
    document.getElementById(`${kind}-size`).textContent =
      `${label} · ${selected[kind].size} · ${kind === "installer" ? "MSI" : "ZIP"}`;
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
architecture.addEventListener("change", updateDownloads);
window.addEventListener("pageshow", updateDownloads);

for (const button of document.querySelectorAll("[data-theme]")) {
  button.addEventListener("click", () => {
    if (button.getAttribute("aria-pressed") === "true") return;
    for (const sibling of document.querySelectorAll("[data-theme]"))
      sibling.setAttribute("aria-pressed", String(sibling === button));
    const image = document.getElementById("hero-screenshot");
    image.src = `assets/main_window_${button.dataset.theme}.png`;
    const tab = button.dataset.theme === "light" ? "Input" : "Output";
    image.alt = `AudioPilot ${tab} tab showing device selection and switch order`;
    const fullsize = document.getElementById("hero-fullsize");
    fullsize.href = image.src;
    fullsize.setAttribute(
      "aria-label",
      `View full-size ${tab} screenshot (opens in a new tab)`,
    );
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
    height: 1020,
    alt: "AudioPilot Routines tab with saved routines and a details pane",
  },
  input: {
    label: "Recording Mixer",
    image: "recording_mixer.png",
    height: 880,
    alt: "AudioPilot Recording Mixer showing the active microphone and recording application controls",
  },
};

for (const button of document.querySelectorAll("[data-view]")) {
  button.addEventListener("click", () => {
    if (button.getAttribute("aria-pressed") === "true") return;
    const view = views[button.dataset.view];
    if (!view) return;
    for (const sibling of document.querySelectorAll("[data-view]"))
      sibling.setAttribute("aria-pressed", String(sibling === button));
    for (const panel of document.querySelectorAll(
      "[data-view-panel], [data-view-caption]",
    )) {
      const active =
        (panel.dataset.viewPanel ?? panel.dataset.viewCaption) ===
        button.dataset.view;
      panel.classList.toggle("is-active", active);
      panel.setAttribute("aria-hidden", String(!active));
      panel.toggleAttribute("inert", !active);
    }
    const image = document.getElementById("detail-screenshot");
    image.src = `assets/${view.image}`;
    image.alt = view.alt;
    image.height = view.height;
    const fullsize = document.getElementById("detail-fullsize");
    fullsize.href = image.src;
    fullsize.setAttribute(
      "aria-label",
      `View full-size ${view.label} screenshot (opens in a new tab)`,
    );
  });
}
