/* =========================================================
   NeoSuite — core.js
   Démarrage, navigation, révélations, palette, dialogues
   ========================================================= */
(function () {
  "use strict";

  var doc = document;
  var body = doc.body;
  var root = doc.documentElement;
  var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var fine = window.matchMedia("(hover: hover) and (pointer: fine)").matches;

  var Neo = (window.Neo = window.Neo || {});
  var readyQueue = [];
  var isReady = false;

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  function normalize(value) {
    return String(value)
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "");
  }

  Neo.reduced = reduced;
  Neo.fine = fine;
  Neo.clamp = clamp;
  Neo.isReady = function () { return isReady; };

  Neo.onReady = function (fn) {
    if (isReady) {
      try { fn(); } catch (error) { console.error(error); }
    } else {
      readyQueue.push(fn);
    }
  };

  /* =========================================================
     Notifications
     ========================================================= */
  function toast(title, message, icon) {
    var wrap = doc.getElementById("toasts");
    if (!wrap) return;

    while (wrap.children.length >= 3) {
      wrap.removeChild(wrap.firstElementChild);
    }

    var el = doc.createElement("div");
    el.className = "toast";
    el.setAttribute("role", "status");
    el.innerHTML =
      '<span class="toast-ico"><svg class="ico" aria-hidden="true"><use href="#' +
      (icon || "i-check-circle") +
      '"/></svg></span>' +
      '<div class="toast-msg"><b></b><span></span></div>';
    el.querySelector("b").textContent = title;
    el.querySelector(".toast-msg span").textContent = message;
    wrap.appendChild(el);

    var removed = false;
    function remove() {
      if (removed) return;
      removed = true;
      el.classList.add("is-out");
      window.setTimeout(function () { el.remove(); }, 380);
    }

    el.addEventListener("click", remove);
    window.setTimeout(remove, 5400);
  }

  Neo.toast = toast;

  /* =========================================================
     Défilement vers une cible
     ========================================================= */
  function goTo(selector, offset) {
    var el = typeof selector === "string" ? doc.querySelector(selector) : selector;
    if (!el) return;
    var top = el.getBoundingClientRect().top + window.scrollY - (offset || 90);
    window.scrollTo({ top: Math.max(0, top), behavior: reduced ? "auto" : "smooth" });
  }

  Neo.goTo = goTo;

  /* =========================================================
     Démarrage
     ========================================================= */
  var boot = doc.getElementById("boot");
  var bootBar = doc.getElementById("bootBar");
  var bootPct = doc.getElementById("bootPct");
  var bootLog = doc.getElementById("bootLog");

  var bootLines = [
    "initialisation du panneau",
    "signature des 9 modules vérifiée",
    "liaison locale établie",
    "aucune télémétrie détectée",
    "prêt."
  ];

  function addBootLine(text) {
    if (!bootLog) return;
    var line = doc.createElement("span");
    line.textContent = text;
    bootLog.appendChild(line);
  }

  function finishBoot() {
    if (isReady) return;
    isReady = true;
    Neo.ready = true;

    if (boot) {
      boot.classList.add("is-done");
      window.setTimeout(function () {
        if (boot && boot.parentNode) boot.parentNode.removeChild(boot);
      }, 900);
    }

    body.classList.remove("is-loading");
    body.classList.add("is-ready");

    initReveals();
    initCounters();
    initTerminal();
    initMagnetic();
    initTilt();

    readyQueue.forEach(function (fn) {
      try { fn(); } catch (error) { console.error(error); }
    });
    readyQueue.length = 0;
  }

  function runBoot() {
    var skip = /(?:\?|&)noboot\b/.test(window.location.search);

    if (skip || !boot || reduced) {
      if (skip && boot) {
        boot.classList.add("is-done");
        window.setTimeout(function () {
          if (boot && boot.parentNode) boot.parentNode.removeChild(boot);
        }, 100);
      }
      window.setTimeout(finishBoot, skip ? 30 : reduced ? 80 : 620);
      return;
    }

    var duration = 1500;
    var started = performance.now();
    var lastLine = -1;

    function frame(now) {
      var progress = clamp((now - started) / duration, 0, 1);
      var eased = 1 - Math.pow(1 - progress, 2.2);

      if (bootBar) bootBar.style.width = (eased * 100).toFixed(1) + "%";
      if (bootPct) bootPct.textContent = String(Math.round(eased * 100)).padStart(2, "0");

      var index = Math.min(bootLines.length - 1, Math.floor(eased * bootLines.length));
      if (index > lastLine) {
        lastLine = index;
        addBootLine(bootLines[index]);
      }

      if (progress < 1) {
        window.requestAnimationFrame(frame);
      } else {
        window.setTimeout(finishBoot, 180);
      }
    }

    window.requestAnimationFrame(frame);
  }

  /* =========================================================
     Révélations
     ========================================================= */
  var revealObserver = null;

  function initReveals() {
    var els = Array.prototype.slice.call(doc.querySelectorAll("[data-reveal]"));
    if (!els.length) return;

    if (!("IntersectionObserver" in window)) {
      els.forEach(function (el) { el.classList.add("is-in"); });
      return;
    }

    revealObserver = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add("is-in");
            revealObserver.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.08, rootMargin: "0px 0px -5% 0px" }
    );

    els.forEach(function (el) { revealObserver.observe(el); });
  }

  /* =========================================================
     Compteurs
     ========================================================= */
  function initCounters() {
    var els = Array.prototype.slice.call(doc.querySelectorAll("[data-count]"));
    if (!els.length) return;

    function animate(el) {
      var target = parseInt(el.getAttribute("data-count"), 10) || 0;
      var pad = parseInt(el.getAttribute("data-pad"), 10) || 0;
      var duration = reduced ? 1 : 1500;
      var started = performance.now();

      function frame(now) {
        var p = clamp((now - started) / duration, 0, 1);
        var eased = 1 - Math.pow(1 - p, 3);
        var value = Math.round(target * eased);
        el.textContent = pad ? String(value).padStart(pad, "0") : String(value);
        if (p < 1) window.requestAnimationFrame(frame);
      }

      window.requestAnimationFrame(frame);
    }

    if (!("IntersectionObserver" in window)) {
      els.forEach(animate);
      return;
    }

    var observer = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            animate(entry.target);
            observer.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.5 }
    );

    els.forEach(function (el) { observer.observe(el); });
  }

  /* =========================================================
     Curseur, lumière, inclinaison
     ========================================================= */
  function initPointer() {
    if (!fine || reduced) return;

    var cursor = doc.querySelector(".cursor");
    var dot = doc.querySelector(".cursor-dot");
    var ring = doc.querySelector(".cursor-ring");
    if (!cursor || !dot || !ring) return;

    body.classList.add("cursor-ready");

    var mx = window.innerWidth / 2;
    var my = window.innerHeight / 2;
    var rx = mx;
    var ry = my;

    window.addEventListener(
      "pointermove",
      function (event) {
        mx = event.clientX;
        my = event.clientY;

        dot.style.transform =
          "translate3d(" + mx + "px," + my + "px,0) translate(-50%,-50%)";

        root.style.setProperty("--mx", ((mx / window.innerWidth) * 100).toFixed(2) + "%");
        root.style.setProperty("--my", ((my / window.innerHeight) * 100).toFixed(2) + "%");

        var hit = event.target.closest
          ? event.target.closest("a, button, summary, input, [data-cursor]")
          : null;
        body.classList.toggle("cursor-hot", !!hit);
      },
      { passive: true }
    );

    window.addEventListener("pointerdown", function () {
      body.classList.add("cursor-down");
    });
    window.addEventListener("pointerup", function () {
      body.classList.remove("cursor-down");
    });

    function follow() {
      rx += (mx - rx) * 0.16;
      ry += (my - ry) * 0.16;
      ring.style.transform =
        "translate3d(" + rx.toFixed(2) + "px," + ry.toFixed(2) + "px,0) translate(-50%,-50%)";
      window.requestAnimationFrame(follow);
    }

    window.requestAnimationFrame(follow);
  }

  function initTilt() {
    if (!fine || reduced) return;

    var tilt = doc.querySelector("[data-tilt]");
    if (!tilt) return;

    var holder = tilt.closest(".hero-visual") || tilt;
    var max = parseFloat(tilt.getAttribute("data-tilt")) || 8;

    holder.addEventListener(
      "pointermove",
      function (event) {
        var rect = tilt.getBoundingClientRect();
        var px = (event.clientX - rect.left) / rect.width - 0.5;
        var py = (event.clientY - rect.top) / rect.height - 0.5;
        tilt.style.transform =
          "rotateY(" + (px * max).toFixed(2) + "deg) rotateX(" +
          (-py * max).toFixed(2) + "deg) translateZ(0)";
      },
      { passive: true }
    );

    holder.addEventListener("pointerleave", function () {
      tilt.style.transform = "";
    });
  }

  function initMagnetic() {
    if (!fine || reduced) return;

    Array.prototype.forEach.call(doc.querySelectorAll(".magnetic"), function (el) {
      el.addEventListener(
        "pointermove",
        function (event) {
          var rect = el.getBoundingClientRect();
          var x = clamp((event.clientX - rect.left - rect.width / 2) * 0.16, -7, 7);
          var y = clamp((event.clientY - rect.top - rect.height / 2) * 0.22, -5, 5);
          el.style.transform = "translate(" + x.toFixed(2) + "px," + y.toFixed(2) + "px)";
        },
        { passive: true }
      );

      el.addEventListener("pointerleave", function () {
        el.style.transform = "";
      });
    });
  }

  /* =========================================================
     Terminal
     ========================================================= */
  function initTerminal() {
    var terminal = doc.querySelector(".terminal");
    var content = doc.querySelector("[data-terminal]");
    if (!terminal || !content) return;

    if (!("IntersectionObserver" in window)) {
      terminal.classList.add("is-live");
      return;
    }

    var observer = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            terminal.classList.add("is-live");
            observer.disconnect();
          }
        });
      },
      { threshold: 0.3 }
    );

    observer.observe(terminal);
  }

  /* =========================================================
     Navigation
     ========================================================= */
  function initNav() {
    var nav = doc.getElementById("nav");
    var burger = doc.querySelector("[data-burger]");
    var menu = doc.getElementById("mobileMenu");
    var progress = doc.querySelector(".scroll-progress span");
    var links = Array.prototype.slice.call(doc.querySelectorAll("[data-nav]"));

    function setMenu(open) {
      body.classList.toggle("menu-open", open);
      if (burger) {
        burger.classList.toggle("is-open", open);
        burger.setAttribute("aria-expanded", open ? "true" : "false");
        burger.setAttribute("aria-label", open ? "Fermer le menu" : "Ouvrir le menu");
      }
      if (menu) menu.setAttribute("aria-hidden", open ? "false" : "true");
    }

    if (burger) {
      burger.addEventListener("click", function () {
        setMenu(!body.classList.contains("menu-open"));
      });
    }

    if (menu) {
      Array.prototype.forEach.call(menu.querySelectorAll("a"), function (link) {
        link.addEventListener("click", function () { setMenu(false); });
      });
    }

    doc.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && body.classList.contains("menu-open")) {
        setMenu(false);
        if (burger) burger.focus();
      }
    });

    var ticking = false;

    function update() {
      ticking = false;
      var y = window.scrollY || window.pageYOffset;
      var max = root.scrollHeight - window.innerHeight;

      if (nav) nav.classList.toggle("is-scrolled", y > 24);
      if (progress) progress.style.transform = "scaleX(" + clamp(max > 0 ? y / max : 0, 0, 1) + ")";

      var current = "";
      links.forEach(function (link) {
        var section = doc.getElementById(link.getAttribute("data-nav"));
        if (!section) return;
        var rect = section.getBoundingClientRect();
        if (rect.top <= window.innerHeight * 0.42 && rect.bottom > window.innerHeight * 0.18) {
          current = link.getAttribute("data-nav");
        }
      });

      links.forEach(function (link) {
        link.classList.toggle("is-active", link.getAttribute("data-nav") === current);
      });
    }

    function onScroll() {
      if (ticking) return;
      ticking = true;
      window.requestAnimationFrame(update);
    }

    window.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll);
    update();
  }

  /* =========================================================
     Journal des nouveautés
     ========================================================= */
  var releaseDialog = doc.getElementById("releaseDialog");

  function openRelease(event) {
    if (event) event.preventDefault();
    if (!releaseDialog) return;
    closePalette();

    try {
      if (typeof releaseDialog.showModal === "function") releaseDialog.showModal();
      else releaseDialog.setAttribute("open", "");
    } catch (error) {
      releaseDialog.setAttribute("open", "");
    }

    body.classList.add("release-open");
  }

  function closeRelease() {
    if (!releaseDialog) return;
    if (typeof releaseDialog.close === "function" && releaseDialog.open) releaseDialog.close();
    else releaseDialog.removeAttribute("open");
    body.classList.remove("release-open");
  }

  function initRelease() {
    if (!releaseDialog) return;

    doc.addEventListener("click", function (event) {
      var opener = event.target.closest("[data-release-open]");
      if (opener) {
        openRelease(event);
        return;
      }
      var closer = event.target.closest("[data-release-close]");
      if (closer) {
        event.preventDefault();
        closeRelease();
      }
    });

    releaseDialog.addEventListener("click", function (event) {
      if (event.target === releaseDialog) closeRelease();
    });

    releaseDialog.addEventListener("close", function () {
      body.classList.remove("release-open");
    });
  }

  /* =========================================================
     Téléchargements : dernière version publiée sur GitHub
     ========================================================= */
  var RELEASE_URL = "https://github.com/head69100/NeoSuite/releases/latest";

  function download() {
    window.open(RELEASE_URL, "_blank", "noopener");
    toast(
      "Téléchargement sur GitHub",
      "Prenez NeoSuite.exe et NeoSuite.pak, gardez-les dans le même dossier, puis lancez NeoSuite.exe.",
      "i-download"
    );
  }

  function initDownloads() {
    doc.addEventListener("click", function (event) {
      var trigger = event.target.closest("[data-download]");
      if (!trigger) return;
      event.preventDefault();
      download();
    });
  }

  /* =========================================================
     Mode Overdrive
     ========================================================= */
  function toggleOverdrive() {
    var on = body.classList.toggle("overdrive");
    if (on) {
      toast(
        "Overdrive activé",
        "Mode démonstration instable. Appuyez à nouveau sur la séquence pour revenir à la normale.",
        "i-bolt"
      );
    } else {
      toast("Retour à la normale", "Le panneau a repris sa configuration standard.", "i-check");
    }
  }

  Neo.toggleOverdrive = toggleOverdrive;

  function initKonami() {
    var sequence = [
      "ArrowUp", "ArrowUp", "ArrowDown", "ArrowDown",
      "ArrowLeft", "ArrowRight", "ArrowLeft", "ArrowRight", "b", "a"
    ];
    var position = 0;

    doc.addEventListener("keydown", function (event) {
      if (event.target && /^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName)) return;
      var key = event.key && event.key.length === 1 ? event.key.toLowerCase() : event.key;
      if (key === sequence[position]) {
        position += 1;
        if (position === sequence.length) {
          position = 0;
          toggleOverdrive();
        }
      } else {
        position = key === sequence[0] ? 1 : 0;
      }
    });
  }

  /* =========================================================
     Palette de commandes
     ========================================================= */
  var palette = doc.getElementById("palette");
  var paletteInput = doc.getElementById("paletteInput");
  var paletteList = doc.getElementById("paletteList");
  var paletteOpen = false;
  var paletteFiltered = [];
  var paletteIndex = 0;
  var lastFocus = null;

  function buildCommands() {
    var commands = [
      { label: "Accueil", hint: "Haut de page", icon: "i-arrow-up", action: function () { goTo("#haut", 0); } },
      { label: "La suite", hint: "Neuf applications", icon: "i-layers", action: function () { goTo("#suite", 0); } },
      { label: "Le mode Boost", hint: "Démo interactive", icon: "i-bolt", action: function () { goTo("#boost"); } },
      { label: "Nos promesses", hint: "Trois engagements", icon: "i-shield", action: function () { goTo("#promesses"); } },
      { label: "Installation", hint: "Trois étapes", icon: "i-download", action: function () { goTo("#installation"); } },
      { label: "Questions fréquentes", hint: "FAQ", icon: "i-spark", action: function () { goTo("#faq"); } },
      { label: "Nouveautés 0.5.0", hint: "Journal des versions", icon: "i-arrow-up", action: function () { openRelease(); } },
      {
        label: "Télécharger NeoSuite",
        hint: "824 Mo · Windows",
        icon: "i-download",
        action: function () {
          download();
        }
      }
    ];

    Array.prototype.forEach.call(doc.querySelectorAll("[data-app]"), function (card, index) {
      var name = card.getAttribute("data-app");
      var category = card.querySelector(".card-cat");
      var icons = {
        NeoSuite: "i-layers",
        NeoDriver: "i-shield",
        NeoBoost: "i-bolt",
        NeoUninstaller: "i-spark",
        NeoSoft: "i-layers",
        NeoRar: "i-arrow-down",
        NeoMedia: "i-play",
        NeoSound: "i-pad",
        NeoTorrent: "i-download"
      };
      commands.push({
        label: name,
        hint: category ? category.textContent.trim() : "Application",
        icon: icons[name] || "i-layers",
        action: function () {
          if (typeof Neo.scrollToApp === "function") Neo.scrollToApp(index);
          else goTo("#suite", 0);
        }
      });
    });

    commands.push({
      label: "Activer le mode Overdrive",
      hint: "Easter egg",
      icon: "i-command",
      action: toggleOverdrive
    });

    return commands;
  }

  var commands = [];

  function updatePaletteActive(scroll) {
    if (!paletteList) return;
    var items = paletteList.querySelectorAll(".palette-item");
    Array.prototype.forEach.call(items, function (item, index) {
      item.setAttribute("aria-selected", index === paletteIndex ? "true" : "false");
    });
    var active = items[paletteIndex];
    if (active && scroll) active.scrollIntoView({ block: "nearest" });
  }

  function renderPalette(query) {
    if (!paletteList) return;
    var q = normalize(query.trim());

    paletteFiltered = commands.filter(function (command) {
      return !q || normalize(command.label + " " + command.hint).indexOf(q) !== -1;
    });

    if (!paletteFiltered.length) {
      paletteIndex = 0;
      paletteList.innerHTML = '<li class="palette-empty">Aucune commande ne correspond.</li>';
      return;
    }

    paletteIndex = clamp(paletteIndex, 0, paletteFiltered.length - 1);
    paletteList.innerHTML = paletteFiltered
      .map(function (command, index) {
        return (
          '<li class="palette-item" role="option" data-index="' + index + '" aria-selected="' +
          (index === paletteIndex ? "true" : "false") + '">' +
          '<span class="palette-item-ico"><svg class="ico" aria-hidden="true"><use href="#' +
          command.icon + '"/></svg></span>' +
          '<span class="palette-item-label">' + command.label + "</span>" +
          '<span class="palette-item-hint">' + command.hint + "</span>" +
          "</li>"
        );
      })
      .join("");

    updatePaletteActive(false);
  }

  function runCommand(command) {
    if (!command) return;
    closePalette();
    window.setTimeout(function () {
      try { command.action(); } catch (error) { console.error(error); }
    }, 160);
  }

  function openPalette() {
    if (!palette || paletteOpen) return;
    paletteOpen = true;
    lastFocus = doc.activeElement;
    palette.hidden = false;
    paletteIndex = 0;
    if (paletteInput) paletteInput.value = "";
    renderPalette("");
    body.classList.add("palette-open");

    window.requestAnimationFrame(function () {
      palette.classList.add("is-open");
    });

    window.setTimeout(function () {
      if (paletteInput) paletteInput.focus();
    }, 50);
  }

  function closePalette() {
    if (!palette || !paletteOpen) return;
    paletteOpen = false;
    palette.classList.remove("is-open");
    body.classList.remove("palette-open");

    window.setTimeout(function () {
      if (!paletteOpen && palette) palette.hidden = true;
    }, 220);

    if (lastFocus && typeof lastFocus.focus === "function") {
      try { lastFocus.focus({ preventScroll: true }); } catch (error) { lastFocus.focus(); }
    }
    lastFocus = null;
  }

  function initPalette() {
    if (!palette || !paletteInput || !paletteList) return;
    commands = buildCommands();

    Array.prototype.forEach.call(doc.querySelectorAll("[data-palette-open]"), function (trigger) {
      trigger.addEventListener("click", function (event) {
        event.preventDefault();
        openPalette();
      });
    });

    var backdrop = palette.querySelector("[data-palette-close]");
    if (backdrop) {
      backdrop.addEventListener("click", closePalette);
    }

    paletteInput.addEventListener("input", function () {
      paletteIndex = 0;
      renderPalette(paletteInput.value);
    });

    paletteInput.addEventListener("keydown", function (event) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        if (!paletteFiltered.length) return;
        paletteIndex = (paletteIndex + 1) % paletteFiltered.length;
        updatePaletteActive(true);
      } else if (event.key === "ArrowUp") {
        event.preventDefault();
        if (!paletteFiltered.length) return;
        paletteIndex = (paletteIndex - 1 + paletteFiltered.length) % paletteFiltered.length;
        updatePaletteActive(true);
      } else if (event.key === "Enter") {
        event.preventDefault();
        runCommand(paletteFiltered[paletteIndex]);
      } else if (event.key === "Escape") {
        event.preventDefault();
        closePalette();
      } else if (event.key === "Tab") {
        event.preventDefault();
      }
    });

    paletteList.addEventListener("click", function (event) {
      var item = event.target.closest(".palette-item");
      if (!item) return;
      var index = parseInt(item.getAttribute("data-index"), 10);
      runCommand(paletteFiltered[index]);
    });

    paletteList.addEventListener("mousemove", function (event) {
      var item = event.target.closest(".palette-item");
      if (!item) return;
      var index = parseInt(item.getAttribute("data-index"), 10);
      if (index === paletteIndex) return;
      paletteIndex = index;
      updatePaletteActive(false);
    });

    doc.addEventListener("keydown", function (event) {
      if ((event.ctrlKey || event.metaKey) && (event.key === "k" || event.key === "K")) {
        event.preventDefault();
        if (paletteOpen) closePalette();
        else openPalette();
      } else if (event.key === "Escape" && paletteOpen) {
        closePalette();
      }
    });

    openPalette._ready = true;
  }

  /* Mode Overdrive : accès depuis la console pour de futures commandes */
  Neo.openPalette = function () { openPalette(); };

  /* =========================================================
     Initialisation
     ========================================================= */
  function initYear() {
    Array.prototype.forEach.call(doc.querySelectorAll("[data-year]"), function (el) {
      el.textContent = String(new Date().getFullYear());
    });
  }

  function init() {
    initPointer();
    initNav();
    initRelease();
    initDownloads();
    initKonami();
    initPalette();
    initYear();
    runBoot();
  }

  if (doc.readyState === "loading") {
    doc.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
