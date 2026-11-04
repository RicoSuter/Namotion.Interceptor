// Phase netted energy counters for the Shelly Pro 3EM (triphase profile), version 1.0.0.
//
// Counts the energy imported and exported after netting the three phases, like a billing meter,
// and publishes it in Wh as the virtual number components TotalImportedEnergy and
// TotalExportedEnergy (found by name, created when missing).
//
// Install: in the device web UI, create a script under Scripts, paste, save, enable "Run on startup", start.
// Behavior and limitations: see the Shelly device docs in the Namotion.Interceptor repository
// (HomeBlaze docs, devices/Shelly.md).

let VERSION = "1.0.0";
let IMPORTED_NAME = "TotalImportedEnergy";
let EXPORTED_NAME = "TotalExportedEnergy";
let KVS_KEY = "phase_netted_energy";
let KVS_NOT_FOUND = -105;
let STATE_VERSION = 1;
let SAMPLE_INTERVAL_MS = 1000;
let SAFETY_CHECK_INTERVAL_MS = 10000;
let PUBLISH_INTERVAL_MS = 15000;
let SAVE_INTERVAL_MS = 60 * 60 * 1000;
let MAX_GAP_MS = 10000;
let START_RETRY_INTERVAL_MS = 5000;
let START_ATTEMPTS = 60; // 5 minutes
let MISSING_PUBLISHES_TO_STOP = 3;
let PUBLISH_TOLERANCE_WH = 0.0005; // half the 0.001 Wh publish rounding step
let DECREASE_TOLERANCE_WH = 0.01; // the saved device values are rounded to 0.001 Wh

let importedId = null;
let exportedId = null;

let imported = 0;
let exported = 0;
let importDebt = 0; // Wh that future import increments pay off before imported grows
let exportDebt = 0; // Wh that future export increments pay off before exported grows
let anchor = null; // { scriptNet, deviceImport, deviceExport } at the last reconcile
let isResuming = false; // continuing from the published values, reconciled against the stored anchor

let lastDevice = null;
let lastPower = null;
let lastUptimeMs = null;
let lastSaveUptimeMs = -SAVE_INTERVAL_MS; // the first reconcile after a start saves
let lastSafetyCheckUptimeMs = 0;
let startAttempts = 0;
let isNumberSetFailureLogged = false;
let isSaveFailureLogged = false;
let missingPublishes = 0;

function log(message) {
  print("[phase-netted-energy] " + message);
}

function stop(message) {
  log(message);
  Shelly.call("Script.Stop", { id: Shelly.getCurrentScriptId() });
}

function round(value) {
  return Math.round(value * 1000) / 1000;
}

function readDeviceCounters() {
  let emData = Shelly.getComponentStatus("emdata", 0);
  if (emData === null || typeof emData.total_act !== "number" || typeof emData.total_act_ret !== "number") return null;
  return { deviceImport: emData.total_act, deviceExport: emData.total_act_ret };
}

function readComponentValue(id) {
  let status = Shelly.getComponentStatus("number", id);
  return status !== null && typeof status.value === "number" ? status.value : null;
}

function scriptNet() {
  return (imported - importDebt) - (exported - exportDebt);
}

function addImport(energy) {
  let paid = Math.min(importDebt, energy);
  importDebt -= paid;
  imported += energy - paid;
}

function addExport(energy) {
  let paid = Math.min(exportDebt, energy);
  exportDebt -= paid;
  exported += energy - paid;
}

// Only called right after the anchor was set, so the counters match it.
function save() {
  let state = {
    v: STATE_VERSION,
    imported: round(imported),
    exported: round(exported),
    importDebt: round(importDebt),
    exportDebt: round(exportDebt),
    deviceImport: round(anchor.deviceImport),
    deviceExport: round(anchor.deviceExport)
  };
  Shelly.call("KVS.Set", { key: KVS_KEY, value: JSON.stringify(state) }, function (result, errorCode, errorMessage) {
    if (errorCode === 0) {
      isSaveFailureLogged = false;
      return;
    }
    if (!isSaveFailureLogged) log("KVS.Set failed: " + errorMessage + " (retried every minute, further failures are not logged)");
    isSaveFailureLogged = true;
    lastSaveUptimeMs = -SAVE_INTERVAL_MS; // saves again at the next reconcile
  });
  lastSaveUptimeMs = Shelly.getUptimeMs();
}

// Only call at start or when the device counters just changed: they update once per minute,
// and a stale device value would push energy into the wrong counter. A correction only touches
// the counter of the direction that dominated the interval: an excess is added to it, a
// shortfall becomes its debt. So no counter ever steps back, and a measurement bias between em
// power and emdata does not show up as flow in the other direction (in minutes with one direction).
// When resuming, the residual is mostly energy of the time the script was stopped, so its own sign
// picks the counter.
function reconcile(device, isStart) {
  // Zero counters are transient at boot or right after a counter reset. Anchoring at them would
  // book the whole lifetime counter as one correction at the next update, so they are skipped; a
  // real reset shows as a decrease at the next non-zero update.
  if (device.deviceImport === 0 && device.deviceExport === 0) return;
  let wasResuming = isResuming;
  isResuming = false;
  let isDecrease = anchor !== null &&
    (device.deviceImport < anchor.deviceImport - DECREASE_TOLERANCE_WH || device.deviceExport < anchor.deviceExport - DECREASE_TOLERANCE_WH);
  if (isDecrease && !isStart) {
    // The post-reset device value was taken at the reset moment, but the script counters
    // already include the energy integrated since. Anchoring here would count that energy
    // again in the next residual, so the next device update anchors instead.
    log("device counters decreased, re-anchoring at the next update without correction");
    anchor = null;
    return;
  }
  let isNewAnchor = anchor === null || isDecrease;
  if (isDecrease) {
    log("device counters decreased, re-anchoring without correction");
  } else if (!isNewAnchor) {
    let deviceNet = (device.deviceImport - device.deviceExport) - (anchor.deviceImport - anchor.deviceExport);
    let residual = deviceNet - (scriptNet() - anchor.scriptNet);
    if (wasResuming ? residual >= 0 : deviceNet >= 0) {
      if (residual > 0) addImport(residual);
      else importDebt -= residual;
    } else {
      if (residual < 0) addExport(-residual);
      else exportDebt += residual;
    }
  }
  anchor = { scriptNet: scriptNet(), deviceImport: device.deviceImport, deviceExport: device.deviceExport };
  // A new anchor is saved at once: a restart against the old one would see a decrease and
  // drop the energy since the last save.
  if (isStart || isNewAnchor || Shelly.getUptimeMs() - lastSaveUptimeMs >= SAVE_INTERVAL_MS) save();
}

function integratePower() {
  let uptimeMs = Shelly.getUptimeMs();
  let em = Shelly.getComponentStatus("em", 0);
  if (em === null || typeof em.total_act_power !== "number") {
    lastPower = null;
    return;
  }
  let deltaMs = uptimeMs - lastUptimeMs;
  if (lastPower !== null && deltaMs > 0 && deltaMs <= MAX_GAP_MS) {
    let energy = (lastPower + em.total_act_power) / 2 * deltaMs / 3600000;
    if (energy > 0) addImport(energy);
    else addExport(-energy);
  }
  lastPower = em.total_act_power;
  lastUptimeMs = uptimeMs;
}

function reconcileOnDeviceUpdate() {
  let device = readDeviceCounters();
  if (device === null || (device.deviceImport === lastDevice.deviceImport && device.deviceExport === lastDevice.deviceExport)) return;
  lastDevice = device;
  reconcile(device, false);
}

// The emdata:0 status event marks the per-minute counter update. Integrating up to that moment
// first keeps the script counters and the device counters at the same point in time.
function onStatus(event) {
  if (event.component !== "emdata:0") return;
  integratePower();
  reconcileOnDeviceUpdate();
}

function sample() {
  integratePower();
  // Fallback in case status events never reach the script.
  if (Shelly.getUptimeMs() - lastSafetyCheckUptimeMs >= SAFETY_CHECK_INTERVAL_MS) {
    lastSafetyCheckUptimeMs = Shelly.getUptimeMs();
    reconcileOnDeviceUpdate();
  }
}

function onNumberSet(result, errorCode, errorMessage) {
  if (errorCode === 0 || isNumberSetFailureLogged) return;
  isNumberSetFailureLogged = true;
  log("Number.Set failed: " + errorMessage + " (further failures are not logged)");
}

// Compares with the value on the device, not the last value sent: a config edit of the
// component (e.g. its meta) resets it to 0, and an idle counter would otherwise never be resent.
// The tolerance keeps a device that stores numbers less precisely from getting a set every time.
// Returns false when the component has no value.
function publishValue(id, value) {
  let deviceValue = readComponentValue(id);
  if (deviceValue === null || Math.abs(deviceValue - value) >= PUBLISH_TOLERANCE_WH) {
    Shelly.call("Number.Set", { id: id, value: value }, onNumberSet);
  }
  return deviceValue !== null;
}

function publish() {
  let isImportedPresent = publishValue(importedId, round(imported));
  let isExportedPresent = publishValue(exportedId, round(exported));
  if (isImportedPresent && isExportedPresent) {
    missingPublishes = 0;
    return;
  }
  // A value can be missing for a moment right after Virtual.Add or during a config edit, so
  // only a value missing on several publishes in a row means the component was deleted.
  missingPublishes++;
  if (missingPublishes >= MISSING_PUBLISHES_TO_STOP) {
    stop("deleted:" + (isImportedPresent ? "" : " number:" + importedId) + (isExportedPresent ? "" : " number:" + exportedId) + ", restart the script");
  }
}

// After a script restart without a device reboot the components usually hold values newer than
// the stored state (compared as published, rounded). Continuing from them keeps the counters
// from stepping back; when a save came after the last publish, the stored state is used instead.
// Returns true when the values were adopted.
function adoptPublishedValues() {
  if (anchor === null) return false;
  let importedValue = readComponentValue(importedId);
  let exportedValue = readComponentValue(exportedId);
  if (importedValue === null || exportedValue === null ||
      importedValue < round(imported) || exportedValue < round(exported)) return false;
  imported = importedValue;
  exported = exportedValue;
  isResuming = true;
  return true;
}

function start() {
  let device = readDeviceCounters();
  if (device === null) {
    stop("emdata:0 disappeared");
    return;
  }
  lastDevice = device;
  if (adoptPublishedValues()) {
    // emdata can be up to 59 s older than the adopted values, so reconciling now would
    // misattribute the energy in between. The next emdata update reconciles against the
    // restored anchor instead.
    log("continuing from the published values");
  } else {
    if (anchor === null) {
      imported = device.deviceImport;
      exported = device.deviceExport;
    }
    reconcile(device, true);
  }
  publish();
  lastSafetyCheckUptimeMs = Shelly.getUptimeMs();
  Shelly.addStatusHandler(onStatus);
  Timer.set(SAMPLE_INTERVAL_MS, true, sample);
  Timer.set(PUBLISH_INTERVAL_MS, true, publish);
  log("version " + VERSION + " started, imported " + round(imported) + " Wh, exported " + round(exported) + " Wh");
}

function addComponent(name, onCreated) {
  let config = { name: name, persisted: false, min: 0, max: 999999999999, meta: { ui: { view: "label", unit: "Wh" } } };
  Shelly.call("Virtual.Add", { type: "number", config: config }, function (result, errorCode, errorMessage) {
    if (errorCode !== 0) {
      stop("Virtual.Add " + name + " failed: " + errorMessage);
      return;
    }
    log("created number:" + result.id + " " + name);
    onCreated(result.id);
  });
}

function ensureComponents() {
  if (importedId === null) {
    addComponent(IMPORTED_NAME, function (id) { importedId = id; ensureComponents(); });
  } else if (exportedId === null) {
    addComponent(EXPORTED_NAME, function (id) { exportedId = id; ensureComponents(); });
  } else {
    start();
  }
}

function findComponents(offset) {
  Shelly.call("Shelly.GetComponents", { dynamic_only: true, include: ["config"], offset: offset }, function (result, errorCode, errorMessage) {
    if (errorCode !== 0) {
      stop("Shelly.GetComponents failed: " + errorMessage);
      return;
    }
    for (let i = 0; i < result.components.length; i++) {
      let component = result.components[i];
      if (component.key.indexOf("number:") !== 0 || !component.config) continue;
      let name = component.config.name;
      let id = component.config.id;
      if (name !== IMPORTED_NAME && name !== EXPORTED_NAME) continue;
      if ((name === IMPORTED_NAME ? importedId : exportedId) !== null) {
        log("duplicate component number:" + id + " " + name + " is ignored");
        continue;
      }
      if (component.config.persisted === true) {
        log("number:" + id + " " + name + " is persisted, so every publish writes to flash; turn persisted off");
      }
      if (name === IMPORTED_NAME) importedId = id;
      else exportedId = id;
    }
    let next = result.offset + result.components.length;
    if (result.components.length > 0 && next < result.total) findComponents(next);
    else ensureComponents();
  });
}

// emdata:0 can be missing for a while after boot. Checking it before the components are created
// leaves no stray components on a device without it.
function waitForDevice() {
  if (readDeviceCounters() !== null) {
    findComponents(0);
    return;
  }
  startAttempts++;
  if (startAttempts < START_ATTEMPTS) Timer.set(START_RETRY_INTERVAL_MS, false, waitForDevice);
  else stop("emdata:0 is not available, the script requires a Pro 3EM in the triphase profile");
}

function restore(value) {
  let stored = null;
  try {
    stored = JSON.parse(value);
  } catch (error) {
    // Unparsable state is reported as invalid below.
  }
  if (stored === null || stored.v !== STATE_VERSION || typeof stored.imported !== "number" || typeof stored.exported !== "number" ||
      typeof stored.deviceImport !== "number" || typeof stored.deviceExport !== "number") {
    log("stored state is invalid, starting at the device counters");
    return;
  }
  imported = stored.imported;
  exported = stored.exported;
  importDebt = typeof stored.importDebt === "number" ? stored.importDebt : 0;
  exportDebt = typeof stored.exportDebt === "number" ? stored.exportDebt : 0;
  anchor = { scriptNet: scriptNet(), deviceImport: stored.deviceImport, deviceExport: stored.deviceExport };
}

Shelly.call("KVS.Get", { key: KVS_KEY }, function (result, errorCode, errorMessage) {
  if (errorCode === KVS_NOT_FOUND) {
    log("no stored state, starting at the device counters");
  } else if (errorCode !== 0) {
    // Starting fresh here would overwrite the stored counters at the first save.
    stop("KVS.Get failed: " + errorMessage);
    return;
  } else {
    restore(result.value);
  }
  waitForDevice();
});
