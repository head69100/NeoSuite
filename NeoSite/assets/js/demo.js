/* =========================================================
   NeoSuite — demo.js
   Console NeoBoost interactive : jauges, courbe, processus
   ========================================================= */
(function () {
  "use strict";

  var doc = document;
  var Neo = (window.Neo = window.Neo || {});
  var clamp = Neo.clamp || function (v, min, max) { return Math.min(max, Math.max(min, v)); };
  var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  var consoleEl = doc.getElementById("boostConsole");
  var boostBtn = doc.getElementById("boostBtn");
  var labelEl = doc.querySelector("[data-boost-label]");
  var stateEl = doc.querySelector("[data-boost-state]");
  var progressEl = doc.querySelector("[data-boost-progress]");
  var freedEl = doc.querySelector("[data-freed]");
  var suspendedEl = doc.querySelector("[data-suspended]");
  var procCountEl = doc.querySelector("[data-proc-count]");
  var fpsEl = doc.querySelector("[data-fps]");
  var logEl = doc.getElementById("boostLog");
  var procList = doc.getElementById("procList");
  var chartCanvas = doc.getElementById("boostChart");

  var procs = procList ? Array.prototype.slice.call(procList.querySelectorAll(".proc")) : [];
  var gauges = Array.prototype.slice.call(doc.querySelectorAll("[data-gauge]"));
  if (!boostBtn || !gauges.length) return;

  /* =========================================================
     État
     ========================================================= */
  var BASE = { cpu: 78, ram: 71, temp: 68 };
  var target = { cpu: BASE.cpu, ram: BASE.ram, temp: BASE.temp };
  var current = { cpu: 0, ram: 0, temp: 0 };
  var rendered = { cpu: -1, ram: -1, temp: -1 };
  var visible = false;
  var started = false;
  var boosted = false;
  var busy = false;
  var timers = [];
  var loopId = 0;
  var chartTimer = 0;
  var jitterTimer = 0;
  var fpsTimer = 0;
  var fpsTarget = 63;

  var RADIUS = 52;
  var CIRCUMFERENCE = 2 * Math.PI * RADIUS;

  var gaugeMap = {};

  gauges.forEach(function (gauge) {
    var fill = gauge.querySelector(".gauge-fill");
    var valueEl = gauge.querySelector("[data-gauge-value]");
    var key = gauge.getAttribute("data-gauge");

    if (fill) {
      fill.style.strokeDasharray = CIRCUMFERENCE.toFixed(2);
      fill.style.strokeDashoffset = CIRCUMFERENCE.toFixed(2);
    }

    gaugeMap[key] = { el: gauge, fill: fill, value: valueEl };
  });

  /* =========================================================
     Processus
     ========================================================= */
  function parseMem(text) {
    var cleaned = String(text).replace(",", ".");
    var match = cleaned.match(/([\d.]+)/);
    if (!match) return 0;
    var value = parseFloat(match[1]);
    if (/go/i.test(cleaned)) return value * 1024;
    return value;
  }

  procs.forEach(function (proc) {
    var mem = proc.querySelector(".proc-mem");
    proc._mem = mem ? parseMem(mem.textContent) : 0;
    proc._load = parseFloat(proc.getAttribute("data-load")) || 0;
    proc._name = proc.querySelector(".proc-name b")
      ? proc.querySelector(".proc-name b").textContent.trim()
      : "Programme";
  });

  function suspendedProcs() {
    return procs.filter(function (proc) {
      return proc.classList.contains("is-suspended");
    });
  }

  function syncStats() {
    var suspended = suspendedProcs();
    var freed = 0;
    var load = 0;

    suspended.forEach(function (proc) {
      freed += proc._mem || 0;
      load += proc._load || 0;
    });

    target.cpu = clamp(BASE.cpu - load, 17, 94);
    target.ram = clamp(BASE.ram - (freed / 16384) * 100, 16, 92);
    target.temp = clamp(BASE.temp - suspended.length * 2.3, 44, 74);
    fpsTarget = 63 + (suspended.length / Math.max(1, procs.length)) * 81;

    if (freedEl) freedEl.textContent = Math.round(freed).toLocaleString("fr-FR");
    if (suspendedEl) suspendedEl.textContent = String(suspended.length);
    if (procCountEl) procCountEl.textContent = String(Math.max(0, procs.length - suspended.length));
  }

  function toggleProc(proc, force) {
    var wasSuspended = proc.classList.contains("is-suspended");
    var next = typeof force === "boolean" ? force : !wasSuspended;
    if (next === wasSuspended) return;

    proc.classList.toggle("is-suspended", next);

    var button = proc.querySelector(".proc-stop");
    if (button) {
      button.textContent = next ? "Reprendre" : "Suspendre";
      button.setAttribute(
        "aria-label",
        (next ? "Reprendre " : "Suspendre ") + proc._name
      );
    }

    syncStats();
  }

  procs.forEach(function (proc) {
    var button = proc.querySelector(".proc-stop");
    if (!button) return;

    button.addEventListener("click", function () {
      if (busy) return;
      var next = !proc.classList.contains("is-suspended");
      toggleProc(proc, next);
      log(
        (next ? "Suspendu — " : "Repris — ") + proc._name,
        next
      );
    });
  });

  /* =========================================================
     Journal de la console
     ========================================================= */
  var logLines = [];

  function log(text, hot) {
    if (!logEl) return;

    logLines.push({ text: text, hot: !!hot });
    if (logLines.length > 2) logLines.shift();

    logEl.innerHTML = "";
    logLines.forEach(function (line) {
      var el = doc.createElement("span");
      el.className = "log-line" + (line.hot ? " is-hot" : "");
      var arrow = doc.createElement("i");
      arrow.textContent = "›";
      el.appendChild(arrow);
      el.appendChild(doc.createTextNode(" " + line.text));
      logEl.appendChild(el);
    });
  }

  function setBadge(text, state) {
    if (!stateEl) return;
    stateEl.textContent = text;
    if (state) stateEl.setAttribute("data-state", state);
    else stateEl.removeAttribute("data-state");
  }

  function setProgress(value) {
    if (progressEl) progressEl.style.transform = "scaleX(" + clamp(value, 0, 1) + ")";
  }

  function setButtonsDisabled(disabled) {
    boostBtn.disabled = disabled;
    procs.forEach(function (proc) {
      var button = proc.querySelector(".proc-stop");
      if (button) button.disabled = disabled;
    });
  }

  /* =========================================================
     Jauges
     ========================================================= */
  function paintGauges() {
    Object.keys(gaugeMap).forEach(function (key) {
      var entry = gaugeMap[key];
      if (!entry) return;
      var value = clamp(current[key], 0, 100);

      if (key === "temp") value = clamp(value, 0, 100);

      if (Math.abs(value - rendered[key]) < 0.05) return;
      rendered[key] = value;

      if (entry.fill) {
        entry.fill.style.strokeDashoffset = (CIRCUMFERENCE * (1 - value / 100)).toFixed(2);
      }
      if (entry.value) {
        entry.value.textContent = String(Math.round(value));
      }
    });
  }

  function loop() {
    loopId = 0;
    if (!visible || doc.hidden) return;

    Object.keys(current).forEach(function (key) {
      var diff = target[key] - current[key];
      if (Math.abs(diff) < 0.06) current[key] = target[key];
      else current[key] += diff * 0.07;
    });

    paintGauges();
    loopId = window.requestAnimationFrame(loop);
  }

  function startLoop() {
    if (!loopId) loopId = window.requestAnimationFrame(loop);
  }

  function stopLoop() {
    if (loopId) window.cancelAnimationFrame(loopId);
    loopId = 0;
  }

  /* =========================================================
     Courbe processeur
     ========================================================= */
  var chartData = [];
  var CHART_POINTS = 46;

  for (var i = 0; i < CHART_POINTS; i += 1) {
    var wave = Math.sin((i / CHART_POINTS) * Math.PI * 2.4) * 11;
    chartData.push(clamp(64 + wave + (Math.random() - 0.5) * 9, 30, 92));
  }

  function chartSize() {
    if (!chartCanvas) return null;
    var width = chartCanvas.clientWidth;
    var height = chartCanvas.clientHeight;
    if (!width || !height) return null;

    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    var pixelWidth = Math.floor(width * dpr);
    var pixelHeight = Math.floor(height * dpr);

    if (chartCanvas.width !== pixelWidth || chartCanvas.height !== pixelHeight) {
      chartCanvas.width = pixelWidth;
      chartCanvas.height = pixelHeight;
    }

    var ctx = chartCanvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx: ctx, width: width, height: height };
  }

  function drawChart() {
    var size = chartSize();
    if (!size) return;

    var ctx = size.ctx;
    var width = size.width;
    var height = size.height;
    var pad = 3;
    var usable = height - pad * 2;

    ctx.clearRect(0, 0, width, height);

    ctx.strokeStyle = "rgba(126, 178, 255, 0.07)";
    ctx.lineWidth = 1;
    for (var g = 1; g <= 3; g += 1) {
      var gy = pad + (usable * g) / 4;
      ctx.beginPath();
      ctx.moveTo(0, gy);
      ctx.lineTo(width, gy);
      ctx.stroke();
    }

    if (!chartData.length) return;

    var points = chartData.map(function (value, index) {
      return {
        x: (index / (chartData.length - 1)) * width,
        y: pad + usable * (1 - clamp(value, 0, 100) / 100)
      };
    });

    var line = new Path2D();
    line.moveTo(points[0].x, points[0].y);

    for (var p = 1; p < points.length - 1; p += 1) {
      var midX = (points[p].x + points[p + 1].x) / 2;
      var midY = (points[p].y + points[p + 1].y) / 2;
      line.quadraticCurveTo(points[p].x, points[p].y, midX, midY);
    }

    line.lineTo(points[points.length - 1].x, points[points.length - 1].y);

    var fill = new Path2D(line);
    fill.lineTo(width, height);
    fill.lineTo(0, height);
    fill.closePath();

    var gradient = ctx.createLinearGradient(0, 0, 0, height);
    gradient.addColorStop(0, "rgba(53, 230, 255, 0.22)");
    gradient.addColorStop(0.7, "rgba(53, 230, 255, 0.05)");
    gradient.addColorStop(1, "rgba(53, 230, 255, 0)");

    ctx.fillStyle = gradient;
    ctx.fill(fill);

    ctx.strokeStyle = "rgba(53, 230, 255, 0.9)";
    ctx.lineWidth = 1.3;
    ctx.lineJoin = "round";
    ctx.lineCap = "round";
    ctx.shadowColor = "rgba(53, 230, 255, 0.6)";
    ctx.shadowBlur = 8;
    ctx.stroke(line);
    ctx.shadowBlur = 0;

    var last = points[points.length - 1];
    ctx.beginPath();
    ctx.arc(last.x, last.y, 2.2, 0, Math.PI * 2);
    ctx.fillStyle = "#8bfaff";
    ctx.shadowColor = "rgba(53, 230, 255, 0.9)";
    ctx.shadowBlur = 10;
    ctx.fill();
    ctx.shadowBlur = 0;
  }

  function pushChart() {
    if (!visible || doc.hidden) return;
    var base = current.cpu || target.cpu;
    var next = clamp(base + (Math.random() - 0.42) * 9, 12, 96);
    chartData.push(next);
    if (chartData.length > CHART_POINTS) chartData.shift();
    drawChart();
  }

  function startChart() {
    drawChart();
    if (chartTimer) return;
    chartTimer = window.setInterval(pushChart, 1700);
  }

  function stopChart() {
    if (chartTimer) window.clearInterval(chartTimer);
    chartTimer = 0;
  }

  /* =========================================================
     FPS + respiration au repos
     ========================================================= */
  function startAmbient() {
    if (jitterTimer) return;

    jitterTimer = window.setInterval(function () {
      if (!visible || doc.hidden || busy) return;
      if (suspendedProcs().length === 0) {
        target.cpu = clamp(BASE.cpu + (Math.random() - 0.5) * 7, 20, 94);
        target.ram = clamp(BASE.ram + (Math.random() - 0.5) * 4, 20, 92);
        target.temp = clamp(BASE.temp + (Math.random() - 0.5) * 2.5, 48, 78);
      }
    }, 2300);

    fpsTimer = window.setInterval(function () {
      if (!visible || doc.hidden || !fpsEl) return;
      var jitter = Math.round((Math.random() - 0.5) * (boosted ? 8 : 5));
      fpsEl.textContent = String(Math.round(fpsTarget) + jitter);
    }, 1250);
  }

  function stopAmbient() {
    if (jitterTimer) window.clearInterval(jitterTimer);
    if (fpsTimer) window.clearInterval(fpsTimer);
    jitterTimer = 0;
    fpsTimer = 0;
  }

  /* =========================================================
     Séquence Boost
     ========================================================= */
  function later(fn, delay) {
    var id = window.setTimeout(fn, delay);
    timers.push(id);
    return id;
  }

  function clearTimers() {
    timers.forEach(function (id) { window.clearTimeout(id); });
    timers = [];
  }

  function finishBoost() {
    busy = false;
    boosted = true;
    setButtonsDisabled(false);
    consoleEl.classList.add("is-boosted");
    setBadge("Boost actif", "on");
    boostBtn.classList.add("is-active");
    boostBtn.setAttribute("aria-pressed", "true");
    if (labelEl) labelEl.textContent = "Restaurer l’état initial";
    setProgress(1);
    log("Boost appliqué — session réversible.", true);
  }

  function startBoost() {
    clearTimers();
    busy = true;
    boosted = true;
    setButtonsDisabled(true);
    consoleEl.classList.add("is-boosted");
    boostBtn.classList.add("is-active");
    boostBtn.setAttribute("aria-pressed", "true");
    if (labelEl) labelEl.textContent = "Optimisation en cours…";
    setBadge("Analyse", "work");
    setProgress(0.08);
    log("Analyse des processus au premier plan…");

    later(function () {
      setProgress(0.22);
      log("Priorité donnée à l’application active…");
    }, 520);

    procs.forEach(function (proc, index) {
      later(function () {
        toggleProc(proc, true);
        setProgress(0.24 + ((index + 1) / procs.length) * 0.68);
        log("Suspendu — " + proc._name);
      }, 900 + index * 330);
    });

    later(finishBoost, 900 + procs.length * 330 + 520);
  }

  function stopBoost() {
    clearTimers();
    busy = true;
    setButtonsDisabled(true);
    setBadge("Restauration", "work");
    if (labelEl) labelEl.textContent = "Restauration…";
    log("Restauration de l’état initial…");

    procs.forEach(function (proc, index) {
      later(function () {
        toggleProc(proc, false);
        setProgress(0.92 - ((index + 1) / procs.length) * 0.72);
        log("Repris — " + proc._name);
      }, 180 + index * 190);
    });

    later(function () {
      busy = false;
      boosted = false;
      setButtonsDisabled(false);
      consoleEl.classList.remove("is-boosted");
      setBadge("Prêt", "");
      boostBtn.classList.remove("is-active");
      boostBtn.setAttribute("aria-pressed", "false");
      if (labelEl) labelEl.textContent = "Activer le mode Boost";
      setProgress(0);
      log("État initial restauré — rien n’a été tué.", true);
    }, 180 + procs.length * 190 + 420);
  }

  boostBtn.addEventListener("click", function () {
    if (busy) return;
    if (boosted) stopBoost();
    else startBoost();
  });

  /* =========================================================
     Visibilité
     ========================================================= */
  function setVisible(next) {
    if (next === visible) return;
    visible = next;

    if (visible) {
      if (!started) {
        started = true;
        current = { cpu: 0, ram: 0, temp: 0 };
        rendered = { cpu: -1, ram: -1, temp: -1 };
        paintGauges();
      }
      startLoop();
      startChart();
      startAmbient();
      if (fpsEl) fpsEl.textContent = String(Math.round(fpsTarget));
      drawChart();
    } else {
      stopLoop();
      stopChart();
      stopAmbient();
    }
  }

  function initGauges() {
    // Première peinture : les anneaux partent de zéro.
    gauges.forEach(function (gauge) {
      var key = gauge.getAttribute("data-gauge");
      var entry = gaugeMap[key];
      if (entry && entry.value) entry.value.textContent = "0";
    });

    if ("IntersectionObserver" in window) {
      var observer = new IntersectionObserver(
        function (entries) {
          setVisible(entries[0].isIntersecting);
        },
        { threshold: 0.18 }
      );
      observer.observe(consoleEl || gauges[0].el);
    } else {
      setVisible(true);
    }
  }

  window.addEventListener("resize", function () {
    drawChart();
  });

  document.addEventListener("visibilitychange", function () {
    if (doc.hidden) {
      stopLoop();
      stopChart();
    } else if (visible) {
      startLoop();
      startChart();
    }
  });

  /* =========================================================
     Démarrage
     ========================================================= */
  syncStats();
  initGauges();
  drawChart();

  if (reduced) {
    // Les transitions sont neutralisées par la CSS ; la démo reste utilisable.
  }
})();
