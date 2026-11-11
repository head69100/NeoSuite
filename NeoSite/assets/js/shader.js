/* =========================================================
   NeoSuite — shader.js
   Aurore boréale WebGL derrière le héros
   ========================================================= */
(function () {
  "use strict";

  var canvas = document.getElementById("heroCanvas");
  if (!canvas) return;

  var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var gl = null;

  try {
    gl = canvas.getContext("webgl", {
      alpha: false,
      antialias: false,
      depth: false,
      stencil: false,
      powerPreference: "high-performance",
      preserveDrawingBuffer: false
    });
  } catch (error) {
    gl = null;
  }

  if (!gl) {
    canvas.style.display = "none";
    return;
  }

  var vertexSource = [
    "attribute vec2 a_position;",
    "void main() {",
    "  gl_Position = vec4(a_position, 0.0, 1.0);",
    "}"
  ].join("\n");

  var fragmentSource = [
    "precision highp float;",
    "",
    "uniform vec2 u_res;",
    "uniform float u_time;",
    "uniform vec2 u_mouse;",
    "",
    "float hash(vec2 p) {",
    "  return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);",
    "}",
    "",
    "float noise(vec2 p) {",
    "  vec2 i = floor(p);",
    "  vec2 f = fract(p);",
    "  f = f * f * (3.0 - 2.0 * f);",
    "  float a = hash(i);",
    "  float b = hash(i + vec2(1.0, 0.0));",
    "  float c = hash(i + vec2(0.0, 1.0));",
    "  float d = hash(i + vec2(1.0, 1.0));",
    "  return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);",
    "}",
    "",
    "float fbm(vec2 p) {",
    "  float value = 0.0;",
    "  float amplitude = 0.5;",
    "  for (int i = 0; i < 5; i++) {",
    "    value += amplitude * noise(p);",
    "    p = p * 2.03 + vec2(3.1, 1.7);",
    "    amplitude *= 0.5;",
    "  }",
    "  return value;",
    "}",
    "",
    "void main() {",
    "  vec2 uv = gl_FragCoord.xy / u_res.xy;",
    "  float aspect = u_res.x / u_res.y;",
    "  vec2 p = vec2(uv.x * aspect, uv.y);",
    "  vec2 mouse = (u_mouse - 0.5) * 0.14;",
    "  float t = u_time * 0.028;",
    "",
    "  float n1 = fbm(p * 1.45 + vec2(t * 0.55, -t * 0.32) + mouse);",
    "  float n2 = fbm(p * 2.35 + vec2(-t * 0.38, t * 0.55) + n1 * 1.55);",
    "  float curtain = smoothstep(0.43, 0.92, n2);",
    "",
    "  float fall = smoothstep(1.02, 0.08, uv.y);",
    "  float side = 0.42 + 0.58 * smoothstep(0.05, 0.78, uv.x);",
    "  float waves = 0.55 + 0.45 * sin(p.x * 3.1 + n1 * 5.0 - t * 2.2);",
    "",
    "  vec3 col = vec3(0.009, 0.015, 0.026);",
    "  col += vec3(0.025, 0.33, 0.54) * curtain * fall * side * waves * 0.72;",
    "  col += vec3(0.06, 0.17, 0.65) * pow(curtain, 2.2) * 0.24;",
    "  col += vec3(0.10, 0.55, 0.70) * pow(smoothstep(0.55, 1.0, n2), 3.0) * 0.14;",
    "",
    "  float glow = 1.0 - length((uv - vec2(0.79, 0.52)) * vec2(0.92, 1.25));",
    "  col += vec3(0.02, 0.28, 0.38) * pow(max(glow, 0.0), 2.4) * 0.28;",
    "",
    "  float left = 1.0 - length((uv - vec2(0.10, 0.30)) * vec2(1.1, 1.4));",
    "  col += vec3(0.05, 0.12, 0.40) * pow(max(left, 0.0), 3.0) * 0.18;",
    "",
    "  vec2 grid = abs(fract(p * 1.6) - 0.5);",
    "  float lines = smoothstep(0.495, 0.5, max(grid.x, grid.y));",
    "  col += vec3(0.05, 0.25, 0.32) * lines * 0.012 * (0.3 + curtain);",
    "",
    "  col *= mix(0.52, 1.0, smoothstep(0.02, 0.62, uv.y));",
    "",
    "  float vignette = smoothstep(1.28, 0.24, length((uv - 0.5) * vec2(1.12, 1.0)));",
    "  col *= 0.60 + 0.50 * vignette;",
    "",
    "  float dither = (hash(gl_FragCoord.xy + fract(u_time * 0.1)) - 0.5) * 0.014;",
    "  col += dither;",
    "",
    "  gl_FragColor = vec4(max(col, vec3(0.0)), 1.0);",
    "}"
  ].join("\n");

  function compile(type, source) {
    var shader = gl.createShader(type);
    if (!shader) return null;
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      console.warn("Shader :", gl.getShaderInfoLog(shader));
      gl.deleteShader(shader);
      return null;
    }
    return shader;
  }

  var vertexShader = compile(gl.VERTEX_SHADER, vertexSource);
  var fragmentShader = compile(gl.FRAGMENT_SHADER, fragmentSource);

  if (!vertexShader || !fragmentShader) {
    canvas.style.display = "none";
    return;
  }

  var program = gl.createProgram();
  gl.attachShader(program, vertexShader);
  gl.attachShader(program, fragmentShader);
  gl.linkProgram(program);

  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    console.warn("Programme WebGL :", gl.getProgramInfoLog(program));
    canvas.style.display = "none";
    return;
  }

  gl.useProgram(program);

  var buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(
    gl.ARRAY_BUFFER,
    new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]),
    gl.STATIC_DRAW
  );

  var positionLocation = gl.getAttribLocation(program, "a_position");
  gl.enableVertexAttribArray(positionLocation);
  gl.vertexAttribPointer(positionLocation, 2, gl.FLOAT, false, 0, 0);

  var resLocation = gl.getUniformLocation(program, "u_res");
  var timeLocation = gl.getUniformLocation(program, "u_time");
  var mouseLocation = gl.getUniformLocation(program, "u_mouse");

  var mouse = [0.5, 0.5];
  var targetMouse = [0.5, 0.5];
  var visible = true;
  var running = false;
  var rafId = 0;
  var started = performance.now();

  function resize() {
    var dpr = Math.min(window.devicePixelRatio || 1, window.innerWidth < 720 ? 1.25 : 1.5);
    var width = Math.max(1, Math.floor(canvas.clientWidth * dpr));
    var height = Math.max(1, Math.floor(canvas.clientHeight * dpr));

    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      gl.viewport(0, 0, width, height);
    }
  }

  function draw(time) {
    resize();
    gl.useProgram(program);

    mouse[0] += (targetMouse[0] - mouse[0]) * 0.045;
    mouse[1] += (targetMouse[1] - mouse[1]) * 0.045;

    gl.uniform2f(resLocation, canvas.width, canvas.height);
    gl.uniform1f(timeLocation, time);
    gl.uniform2f(mouseLocation, mouse[0], mouse[1]);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  function loop(now) {
    if (!visible || document.hidden) {
      running = false;
      return;
    }
    draw((now - started) / 1000);
    rafId = window.requestAnimationFrame(loop);
  }

  function start() {
    if (running || reduced) return;
    running = true;
    rafId = window.requestAnimationFrame(loop);
  }

  function stop() {
    running = false;
    window.cancelAnimationFrame(rafId);
  }

  window.addEventListener(
    "pointermove",
    function (event) {
      var rect = canvas.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      targetMouse[0] = (event.clientX - rect.left) / rect.width;
      targetMouse[1] = 1 - (event.clientY - rect.top) / rect.height;
    },
    { passive: true }
  );

  window.addEventListener("resize", function () {
    resize();
    if (reduced) draw(6);
  });

  document.addEventListener("visibilitychange", function () {
    if (document.hidden) stop();
    else start();
  });

  if ("IntersectionObserver" in window) {
    var hero = document.querySelector(".hero");
    if (hero) {
      new IntersectionObserver(
        function (entries) {
          visible = entries[0].isIntersecting;
          if (visible) start();
          else stop();
        },
        { threshold: 0.02 }
      ).observe(hero);
    }
  }

  canvas.addEventListener("webglcontextlost", function (event) {
    event.preventDefault();
    stop();
  });

  canvas.addEventListener("webglcontextrestored", function () {
    window.location.reload();
  });

  if (reduced) {
    resize();
    draw(8);
    return;
  }

  resize();
  draw(0);

  if (window.Neo && typeof window.Neo.onReady === "function") {
    window.Neo.onReady(start);
  } else {
    start();
  }
})();
