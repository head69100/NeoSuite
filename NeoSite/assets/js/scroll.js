/* =========================================================
   NeoSuite — scroll.js
   Défilement horizontal épinglé, texte allumé, lien palette
   ========================================================= */
(function () {
  "use strict";

  var doc = document;
  var Neo = (window.Neo = window.Neo || {});
  var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var clamp = Neo.clamp || function (v, min, max) { return Math.min(max, Math.max(min, v)); };

  var section = doc.querySelector("[data-suite]");
  var pin = section ? section.querySelector(".suite-pin") : null;
  var viewport = section ? section.querySelector(".suite-viewport") : null;
  var track = doc.querySelector("[data-suite-track]");
  var cards = track ? Array.prototype.slice.call(track.querySelectorAll(".app-card")) : [];
  var currentEl = doc.querySelector("[data-suite-current]");
  var appEl = doc.querySelector("[data-suite-app]");
  var barEl = doc.querySelector("[data-suite-bar]");

  var distance = 0;
  var progress = 0;
  var lastIndex = -1;
  var ticking = false;
  var resizeTimer = 0;

  function measure() {
    if (!section || !viewport || !track || !cards.length) return;

    if (reduced) {
      section.style.height = "";
      track.style.transform = "";
      return;
    }

    var last = cards[cards.length - 1];
    var lastLeft = last.offsetLeft;
    var lastWidth = last.offsetWidth;

    distance = Math.max(0, lastLeft + lastWidth - viewport.clientWidth + 24);

    if (distance < 8) {
      section.style.height = "";
      distance = 0;
      track.style.transform = "";
      return;
    }

    section.style.height = window.innerHeight + distance + "px";
    update(true);
  }

  function update(force) {
    if (!section || !track || !cards.length) return;

    if (reduced || !distance) {
      progress = 0;
    } else {
      var total = section.offsetHeight - window.innerHeight;
      var top = section.getBoundingClientRect().top;
      progress = clamp(total > 0 ? -top / total : 0, 0, 1);
    }

    track.style.transform = "translate3d(" + (-progress * distance).toFixed(2) + "px,0,0)";

    var index = Math.round(progress * (cards.length - 1));
    if (index !== lastIndex || force) {
      lastIndex = index;
      if (currentEl) currentEl.textContent = String(index + 1).padStart(2, "0");
      if (appEl) {
        var name = cards[index] ? cards[index].getAttribute("data-app") : "";
        appEl.textContent = name;
      }
    }

    if (barEl) {
      var scale = 1 / cards.length + progress * (1 - 1 / cards.length);
      barEl.style.transform = "scaleX(" + scale.toFixed(4) + ")";
    }
  }

  function onScroll() {
    if (ticking) return;
    ticking = true;
    window.requestAnimationFrame(function () {
      ticking = false;
      update(false);
    });
  }

  function onResize() {
    window.clearTimeout(resizeTimer);
    resizeTimer = window.setTimeout(measure, 140);
  }

  /* Défilement direct vers une application (palette) */
  Neo.scrollToApp = function (index) {
    if (!section || !cards.length) return;

    if (!distance || reduced) {
      var card = cards[clamp(index, 0, cards.length - 1)];
      if (card) Neo.goTo(card, 160);
      return;
    }

    var total = section.offsetHeight - window.innerHeight;
    var ratio = cards.length > 1 ? index / (cards.length - 1) : 0;
    var target = section.offsetTop + ratio * total;

    window.scrollTo({
      top: target,
      behavior: reduced ? "auto" : "smooth"
    });
  };

  /* =========================================================
     Manifeste : allumage mot à mot
     ========================================================= */
  function splitWords(el) {
    var walker = doc.createTreeWalker(el, NodeFilter.SHOW_TEXT, null, false);
    var textNodes = [];
    var node;

    while ((node = walker.nextNode())) {
      if (node.nodeValue && node.nodeValue.trim()) textNodes.push(node);
    }

    textNodes.forEach(function (textNode) {
      var fragment = doc.createDocumentFragment();
      var parts = textNode.nodeValue.split(/(\s+)/);

      parts.forEach(function (part) {
        if (!part) return;
        if (/^\s+$/.test(part)) {
          fragment.appendChild(doc.createTextNode(part));
        } else {
          var span = doc.createElement("span");
          span.className = "w";
          span.textContent = part;
          fragment.appendChild(span);
        }
      });

      if (textNode.parentNode) textNode.parentNode.replaceChild(fragment, textNode);
    });

    return Array.prototype.slice.call(el.querySelectorAll(".w"));
  }

  function initManifesto() {
    var text = doc.querySelector(".manifesto-text");
    if (!text) return;

    var words = splitWords(text);
    if (!words.length) return;

    var lit = -1;

    function light() {
      var rect = text.getBoundingClientRect();
      var start = window.innerHeight * 0.84;
      var span = rect.height + window.innerHeight * 0.12;
      var p = clamp((start - rect.top) / span, 0, 1);
      var count = Math.round(p * words.length);

      if (count === lit) return;
      lit = count;

      words.forEach(function (word, index) {
        word.classList.toggle("is-lit", index < count);
      });
    }

    window.addEventListener("scroll", light, { passive: true });
    window.addEventListener("resize", light);
    light();
  }

  /* =========================================================
     Initialisation
     ========================================================= */
  function setup() {
    initManifesto();

    if (section && track && viewport) {
      measure();
      update(true);

      window.addEventListener("scroll", onScroll, { passive: true });
      window.addEventListener("resize", onResize);

      if ("ResizeObserver" in window) {
        var observer = new ResizeObserver(function () { onResize(); });
        observer.observe(viewport);
      }

      if (doc.fonts && doc.fonts.ready) {
        doc.fonts.ready.then(function () { measure(); });
      }

      window.addEventListener("load", measure);
    }
  }

  if (Neo.onReady) Neo.onReady(setup);
  else setup();
})();
