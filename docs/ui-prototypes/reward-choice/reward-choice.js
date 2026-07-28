/* Canonical reward-choice browser interaction scaffold.
 *
 * Source grounding for the default host data:
 * - abilityStats: server/src/progression.rs AllocatedStatTotals
 * - spellSchools: docs/reward-choice-flow-design-2026-07-25.md current
 *   player-facing roster (Frost is display copy for internal COLD)
 * - combatDisciplines: server/src/progression_catalog.shared.json
 *
 * The collections are inputs, not individually wired controls. The future
 * runtime host can supply the same shape without changing the screen layout.
 */
(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var stage = $("Stage");
  var reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  var prototypeHostData = {
    abilityPointBudget: 5,
    abilityStats: [
      { stableId: "MIGHT", displayName: "Might", baseValue: 2, glyph: "M", sortOrder: 10 },
      { stableId: "INSIGHT", displayName: "Insight", baseValue: 1, glyph: "◈", sortOrder: 20 },
      { stableId: "FINESSE", displayName: "Finesse", baseValue: 0, glyph: "⌁", sortOrder: 30 },
      { stableId: "QUICKNESS", displayName: "Quickness", baseValue: 1, glyph: "↯", sortOrder: 40 },
      { stableId: "FORTITUDE", displayName: "Fortitude", baseValue: 1, glyph: "✦", sortOrder: 50 }
    ],
    spellSchools: [
      { stableId: "AIR", displayName: "Air", artClass: "art-school-air", sortOrder: 10 },
      { stableId: "ARCANE", displayName: "Arcane", artClass: "art-school-arcane", sortOrder: 20 },
      { stableId: "COLD", displayName: "Frost", artClass: "art-school-cold", sortOrder: 30 },
      { stableId: "FIRE", displayName: "Fire", artClass: "art-school-fire", sortOrder: 40 },
      { stableId: "HOLY", displayName: "Holy", artClass: "art-school-holy", sortOrder: 50 },
      { stableId: "LIGHTNING", displayName: "Lightning", artClass: "art-school-lightning", sortOrder: 60 },
      {
        stableId: "NECROMANCY",
        displayName: "Necromancy",
        artClass: "art-school-necromancy",
        sortOrder: 70
      },
      {
        stableId: "SHADOW",
        displayName: "Shadow",
        artClass: "art-school-shadow",
        sortOrder: 80
      }
    ],
    combatDisciplines: [
      {
        stableId: "SUBTLETY",
        displayName: "Subtlety",
        combatProfileId: "DAGGERS",
        artClass: "art-discipline-subtlety",
        sortOrder: 10
      },
      {
        stableId: "WAR",
        displayName: "War",
        combatProfileId: "TWO_HANDED_SWORD",
        artClass: "art-discipline-war",
        sortOrder: 20
      },
      {
        stableId: "ZEAL",
        displayName: "Zeal",
        combatProfileId: "SWORD_AND_SHIELD",
        artClass: "art-discipline-zeal",
        sortOrder: 30
      },
      {
        stableId: "PRECISION",
        displayName: "Precision",
        combatProfileId: "ARCHER_BOW",
        artClass: "art-discipline-precision",
        sortOrder: 40
      },
      {
        stableId: "ARCANA",
        displayName: "Arcana",
        combatProfileId: "STAFF",
        artClass: "art-discipline-arcana",
        sortOrder: 50
      }
    ]
  };

  var profileArtById = {
    DAGGERS: "art-discipline-subtlety",
    TWO_HANDED_SWORD: "art-discipline-war",
    SWORD_AND_SHIELD: "art-discipline-zeal",
    ARCHER_BOW: "art-discipline-precision",
    STAFF: "art-discipline-arcana"
  };

  var state = {
    allocations: {},
    pathKind: "",
    pathChoiceId: "",
    dice: {
      active: false,
      held: false,
      startedAt: 0,
      result: 0,
      frame: 0,
      requestId: "",
      profile: null
    }
  };

  var diceProfiles = [
    {
      id: "crescent",
      anticipation: 0.28,
      moving: 1.22,
      settleStart: 0.67,
      turns: 4.6,
      horizontal: [[0, -0.28], [0.18, -0.36], [0.46, 0.26], [0.72, -0.12], [1, 0]],
      vertical: [[0, -0.18], [0.20, 0.23], [0.49, 0.16], [0.76, -0.09], [1, 0]],
      depth: [[0, 0.42], [0.25, -0.24], [0.54, 0.32], [0.81, -0.10], [1, 0]],
      scale: [[0, 0.68], [0.19, 0.92], [0.48, 1.08], [0.76, 0.94], [1, 1]]
    },
    {
      id: "crosswind",
      anticipation: 0.32,
      moving: 1.30,
      settleStart: 0.70,
      turns: 5.2,
      horizontal: [[0, 0.30], [0.17, 0.36], [0.43, -0.30], [0.69, 0.16], [1, 0]],
      vertical: [[0, -0.13], [0.18, -0.20], [0.45, 0.24], [0.72, -0.08], [1, 0]],
      depth: [[0, 0.34], [0.24, -0.22], [0.52, 0.42], [0.78, -0.12], [1, 0]],
      scale: [[0, 0.73], [0.18, 0.88], [0.52, 1.10], [0.80, 0.95], [1, 1]]
    },
    {
      id: "helix",
      anticipation: 0.24,
      moving: 1.38,
      settleStart: 0.72,
      turns: 5.8,
      horizontal: [[0, -0.17], [0.18, -0.32], [0.40, 0.29], [0.63, -0.22], [0.82, 0.10], [1, 0]],
      vertical: [[0, -0.25], [0.19, 0.19], [0.43, 0.27], [0.66, -0.16], [0.84, 0.07], [1, 0]],
      depth: [[0, 0.48], [0.20, -0.30], [0.46, 0.38], [0.69, -0.18], [0.85, 0.16], [1, 0]],
      scale: [[0, 0.66], [0.17, 0.90], [0.45, 1.12], [0.72, 0.92], [0.86, 1.04], [1, 1]]
    }
  ];

  function sorted(collection) {
    return collection.slice().sort(function (a, b) {
      return a.sortOrder - b.sortOrder || a.stableId.localeCompare(b.stableId);
    });
  }

  function fitStage() {
    var wrap = stage.parentElement;
    var scale = Math.min(wrap.clientWidth / 1920, wrap.clientHeight / 1080);
    stage.style.transform = "scale(" + scale + ")";
  }

  function paintBackdrop() {
    var ctx = $("Backdrop").getContext("2d");
    var width = ctx.canvas.width;
    var height = ctx.canvas.height;
    var base = ctx.createLinearGradient(0, 0, 0, height);
    base.addColorStop(0, "#11161a");
    base.addColorStop(0.48, "#090d10");
    base.addColorStop(1, "#0a0a09");
    ctx.fillStyle = base;
    ctx.fillRect(0, 0, width, height);

    var blue = ctx.createRadialGradient(910, 500, 30, 910, 500, 530);
    blue.addColorStop(0, "rgba(36, 104, 145, 0.20)");
    blue.addColorStop(0.58, "rgba(18, 50, 68, 0.08)");
    blue.addColorStop(1, "rgba(0, 0, 0, 0)");
    ctx.fillStyle = blue;
    ctx.fillRect(0, 0, width, height);

    var amber = ctx.createRadialGradient(1470, 510, 40, 1470, 510, 520);
    amber.addColorStop(0, "rgba(145, 95, 29, 0.15)");
    amber.addColorStop(0.6, "rgba(72, 46, 20, 0.06)");
    amber.addColorStop(1, "rgba(0, 0, 0, 0)");
    ctx.fillStyle = amber;
    ctx.fillRect(0, 0, width, height);

    var seed = 146959810;
    for (var i = 0; i < 480; i++) {
      seed = (seed * 1664525 + 1013904223) >>> 0;
      var x = seed % width;
      seed = (seed * 1664525 + 1013904223) >>> 0;
      var y = seed % height;
      seed = (seed * 1664525 + 1013904223) >>> 0;
      var alpha = 0.018 + (seed % 12) / 1000;
      ctx.fillStyle = "rgba(214, 205, 184, " + alpha + ")";
      ctx.fillRect(x, y, 1 + (seed % 3), 1);
    }

    ctx.strokeStyle = "rgba(176, 154, 108, 0.055)";
    ctx.lineWidth = 1;
    for (var line = 0; line < 34; line++) {
      var y0 = 45 + line * 31;
      ctx.beginPath();
      ctx.moveTo(30, y0);
      ctx.bezierCurveTo(480, y0 - 16, 1120, y0 + 22, 1890, y0 - 8);
      ctx.stroke();
    }

    var vignette = ctx.createRadialGradient(960, 520, 360, 960, 520, 1180);
    vignette.addColorStop(0, "rgba(0, 0, 0, 0)");
    vignette.addColorStop(0.72, "rgba(0, 0, 0, 0.25)");
    vignette.addColorStop(1, "rgba(0, 0, 0, 0.82)");
    ctx.fillStyle = vignette;
    ctx.fillRect(0, 0, width, height);
  }

  function spentPoints() {
    return Object.keys(state.allocations).reduce(function (sum, key) {
      return sum + state.allocations[key];
    }, 0);
  }

  function remainingPoints() {
    return prototypeHostData.abilityPointBudget - spentPoints();
  }

  function renderStats() {
    var list = $("StatList");
    list.textContent = "";
    sorted(prototypeHostData.abilityStats).forEach(function (stat) {
      if (state.allocations[stat.stableId] === undefined) {
        state.allocations[stat.stableId] = 0;
      }

      var row = document.createElement("div");
      row.className = "stat-row";
      row.dataset.statId = stat.stableId;

      var icon = document.createElement("div");
      icon.className = "stat-icon stat-" + stat.stableId.toLowerCase();
      icon.setAttribute("aria-hidden", "true");
      var glyph = document.createElement("span");
      glyph.className = "stat-glyph";
      glyph.textContent = stat.glyph;
      icon.appendChild(glyph);

      var name = document.createElement("div");
      name.className = "stat-name";
      name.textContent = stat.displayName;

      var value = document.createElement("output");
      value.className = "stat-value";
      value.id = "StatValue" + stat.stableId;
      value.setAttribute("aria-label", stat.displayName + " value");

      var decrement = document.createElement("button");
      decrement.className = "stat-step";
      decrement.type = "button";
      decrement.textContent = "−";
      decrement.dataset.delta = "-1";
      decrement.setAttribute("aria-label", "Decrease " + stat.displayName);

      var increment = document.createElement("button");
      increment.className = "stat-step";
      increment.type = "button";
      increment.textContent = "+";
      increment.dataset.delta = "1";
      increment.setAttribute("aria-label", "Increase " + stat.displayName);

      decrement.addEventListener("click", function () { adjustStat(stat.stableId, -1); });
      increment.addEventListener("click", function () { adjustStat(stat.stableId, 1); });

      row.appendChild(icon);
      row.appendChild(name);
      row.appendChild(value);
      row.appendChild(decrement);
      row.appendChild(increment);
      list.appendChild(row);
    });
    refreshStats();
  }

  function refreshStats() {
    var remaining = remainingPoints();
    $("PointsRemaining").textContent = remaining;
    sorted(prototypeHostData.abilityStats).forEach(function (stat) {
      var allocation = state.allocations[stat.stableId] || 0;
      var row = $("StatList").querySelector('[data-stat-id="' + stat.stableId + '"]');
      if (!row) return;
      row.querySelector(".stat-value").textContent = stat.baseValue + allocation;
      row.querySelector('[data-delta="-1"]').disabled = allocation <= 0;
      row.querySelector('[data-delta="1"]').disabled = remaining <= 0;
    });
  }

  function adjustStat(stableId, delta) {
    var current = state.allocations[stableId] || 0;
    if (delta > 0 && remainingPoints() <= 0) return;
    if (delta < 0 && current <= 0) return;
    state.allocations[stableId] = current + delta;
    refreshStats();
    announce(
      remainingPoints() + " ability point" +
      (remainingPoints() === 1 ? "" : "s") + " remaining."
    );
  }

  function resetPoints() {
    Object.keys(state.allocations).forEach(function (key) {
      state.allocations[key] = 0;
    });
    refreshStats();
    announce("Ability points reset. " + remainingPoints() + " remaining.");
  }

  function makePathOption(kind, option) {
    var button = document.createElement("button");
    button.className = "path-option";
    button.type = "button";
    button.dataset.kind = kind;
    button.dataset.choiceId = option.stableId;
    button.setAttribute("aria-pressed", "false");
    button.setAttribute("aria-label", option.displayName);
    button.title = option.displayName;

    var halo = document.createElement("div");
    halo.className = "option-halo";
    halo.setAttribute("aria-hidden", "true");

    var medallion = document.createElement("div");
    medallion.className = "option-medallion";
    var art = document.createElement("div");
    art.className = "option-art " + option.artClass;
    medallion.appendChild(art);

    var label = document.createElement("div");
    label.className = "option-label";
    label.textContent = option.displayName.toUpperCase();

    button.appendChild(halo);
    button.appendChild(medallion);
    button.appendChild(label);
    button.addEventListener("click", function () {
      selectPathOption(kind, option.stableId, true);
    });
    return button;
  }

  function renderOrbit(kind, collection, hostId, canvasId) {
    var host = $(hostId);
    host.textContent = "";
    sorted(collection).forEach(function (option) {
      host.appendChild(makePathOption(kind, option));
    });
    layoutOrbit(host, $(canvasId));
    if (state.pathKind === kind && state.pathChoiceId) {
      var selected = host.querySelector(
        '.path-option[data-choice-id="' + state.pathChoiceId + '"]'
      );
      if (selected) {
        selected.classList.add("is-selected");
        selected.setAttribute("aria-pressed", "true");
      }
    }
  }

  function layoutOrbit(host, canvas) {
    var buttons = Array.prototype.slice.call(host.querySelectorAll(".path-option"));
    var centerX = canvas.width / 2;
    var centerY = 262;
    var radiusX = buttons.length > 6 ? 196 : 184;
    var radiusY = buttons.length > 6 ? 184 : 178;
    var buttonWidth = 108;
    var medallionRadius = 48;
    var coreRadius = 80;
    var ctx = canvas.getContext("2d");
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    buttons.forEach(function (button, index) {
      var angle = -Math.PI / 2 + (Math.PI * 2 * index / buttons.length);
      var x = centerX + Math.cos(angle) * radiusX;
      var y = centerY + Math.sin(angle) * radiusY;
      button.style.left = Math.round(x - buttonWidth / 2) + "px";
      button.style.top = Math.round(y - medallionRadius) + "px";

      var spell = button.dataset.kind === "school";
      var dx = Math.cos(angle);
      var dy = Math.sin(angle);
      var startX = centerX + dx * coreRadius;
      var startY = centerY + dy * coreRadius;
      var endX = x - dx * (medallionRadius + 3);
      var endY = y - dy * (medallionRadius + 3);
      var chainColor = spell ? "rgba(90, 194, 238, 0.72)" : "rgba(207, 153, 68, 0.72)";
      var chainDark = spell ? "rgba(7, 35, 48, 0.94)" : "rgba(47, 31, 10, 0.94)";

      ctx.beginPath();
      ctx.moveTo(startX, startY);
      ctx.lineTo(endX, endY);
      ctx.strokeStyle = chainDark;
      ctx.lineWidth = 5;
      ctx.stroke();

      ctx.beginPath();
      ctx.moveTo(startX, startY);
      ctx.lineTo(endX, endY);
      ctx.strokeStyle = chainColor;
      ctx.lineWidth = 1.25;
      ctx.stroke();

      var chainLength = Math.sqrt(
        Math.pow(endX - startX, 2) + Math.pow(endY - startY, 2)
      );
      var links = Math.max(2, Math.floor(chainLength / 20));
      for (var link = 1; link < links; link++) {
        var t = link / links;
        var linkX = startX + (endX - startX) * t;
        var linkY = startY + (endY - startY) * t;
        ctx.beginPath();
        ctx.ellipse(
          linkX,
          linkY,
          6,
          3.2,
          angle + (link % 2 === 0 ? 0 : Math.PI / 2),
          0,
          Math.PI * 2
        );
        ctx.fillStyle = chainDark;
        ctx.fill();
        ctx.strokeStyle = chainColor;
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    });

    ctx.beginPath();
    ctx.arc(centerX, centerY, 5, 0, Math.PI * 2);
    ctx.fillStyle = buttons[0] && buttons[0].dataset.kind === "school"
      ? "rgba(139, 226, 255, 0.82)"
      : "rgba(245, 194, 104, 0.82)";
    ctx.fill();
  }

  function optionFor(kind, stableId) {
    var collection = kind === "school"
      ? prototypeHostData.spellSchools
      : prototypeHostData.combatDisciplines;
    return collection.find(function (candidate) {
      return candidate.stableId === stableId;
    });
  }

  function selectPathOption(kind, stableId, focus) {
    var option = optionFor(kind, stableId);
    if (!option) return;
    state.pathKind = kind;
    state.pathChoiceId = stableId;

    document.querySelectorAll(".path-option").forEach(function (button) {
      var selected = button.dataset.kind === kind && button.dataset.choiceId === stableId;
      button.classList.toggle("is-selected", selected);
      button.setAttribute("aria-pressed", selected ? "true" : "false");
    });
    $("SpellPanel").classList.toggle("is-selected-path", kind === "school");
    $("DisciplinePanel").classList.toggle("is-selected-path", kind === "discipline");
    $("SpellSelection").textContent = kind === "school"
      ? option.displayName + " selected"
      : "Choose a spellcasting school";
    $("DisciplineSelection").textContent = kind === "discipline"
      ? option.displayName + " selected"
      : "Choose a combat discipline";
    $("ConfirmRoll").disabled = false;

    var selectedButton = document.querySelector(
      '.path-option[data-kind="' + kind + '"][data-choice-id="' + stableId + '"]'
    );
    if (selectedButton) {
      selectedButton.classList.remove("is-settling");
      void selectedButton.offsetWidth;
      selectedButton.classList.add("is-settling");
      setTimeout(function () {
        selectedButton.classList.remove("is-settling");
      }, 260);
      if (focus) selectedButton.focus();
    }
    announce(option.displayName + " selected. Confirm when ready.");
  }

  function announce(message) {
    $("Instruction").textContent = message;
  }

  function canonicalDisciplineFallback(current) {
    return current.map(function (item) {
      return Object.assign({}, item);
    });
  }

  function loadCanonicalDisciplines() {
    var sourceUrl = new URL(
      "../../../server/src/progression_catalog.shared.json",
      window.location.href
    );
    return fetch(sourceUrl)
      .then(function (response) {
        if (!response.ok) throw new Error("Catalog fetch failed");
        return response.json();
      })
      .then(function (catalog) {
        if (!Array.isArray(catalog.combat_disciplines) ||
            catalog.combat_disciplines.length === 0) {
          throw new Error("Catalog has no combat disciplines");
        }
        prototypeHostData.combatDisciplines = catalog.combat_disciplines.map(function (entry) {
          return {
            stableId: entry.discipline_id,
            displayName: entry.display_name,
            combatProfileId: entry.combat_profile_id,
            artClass: profileArtById[entry.combat_profile_id] || "",
            sortOrder: entry.sort_order
          };
        });
        renderOrbit(
          "discipline",
          prototypeHostData.combatDisciplines,
          "CombatDisciplineOptions",
          "DisciplineLines"
        );
      })
      .catch(function () {
        prototypeHostData.combatDisciplines =
          canonicalDisciplineFallback(prototypeHostData.combatDisciplines);
      });
  }

  function randomD20() {
    if (window.crypto && window.crypto.getRandomValues) {
      var value = new Uint32Array(1);
      window.crypto.getRandomValues(value);
      return 1 + (value[0] % 20);
    }
    return 1 + Math.floor(Math.random() * 20);
  }

  function hashText(text) {
    var hash = 2166136261;
    for (var i = 0; i < text.length; i++) {
      hash ^= text.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return hash >>> 0;
  }

  function curveValue(keys, time) {
    if (time <= keys[0][0]) return keys[0][1];
    for (var i = 1; i < keys.length; i++) {
      if (time <= keys[i][0]) {
        var start = keys[i - 1];
        var end = keys[i];
        var range = end[0] - start[0];
        var t = range > 0 ? (time - start[0]) / range : 1;
        t = t * t * (3 - 2 * t);
        return start[1] + (end[1] - start[1]) * t;
      }
    }
    return keys[keys.length - 1][1];
  }

  function resolvedRollFromQuery() {
    var requested = Number(new URLSearchParams(window.location.search).get("roll"));
    return requested >= 1 && requested <= 20 ? Math.floor(requested) : randomD20();
  }

  function beginDiceRoll() {
    if (!state.pathChoiceId || state.dice.active) return;
    var requestId = "level-up-preview-" + Date.now();
    var result = resolvedRollFromQuery();
    var profile = diceProfiles[hashText(requestId) % diceProfiles.length];
    state.dice.active = true;
    state.dice.held = false;
    state.dice.startedAt = performance.now();
    state.dice.result = result;
    state.dice.requestId = requestId;
    state.dice.profile = profile;
    $("DiceOverlay").classList.add("is-active");
    $("DiceOverlay").setAttribute("aria-hidden", "false");
    $("DiceResult").textContent = "";
    $("DiceStatus").querySelector(".dice-status-kicker").textContent = "FATE IS TURNING";
    $("DiceHint").textContent = "Click the die to skip to the result";
    $("ConfirmRoll").disabled = true;
    cancelAnimationFrame(state.dice.frame);
    drawDiceFrame(state.dice.startedAt);
  }

  function enterHeldDice() {
    state.dice.held = true;
    $("DiceStatus").querySelector(".dice-status-kicker").textContent = "THE DIE IS CAST";
    $("DiceResult").textContent = "D20  ·  " + state.dice.result;
    $("DiceHint").textContent = "Click to dismiss the prototype result";
    drawDie(1, true);
  }

  function dismissDice() {
    cancelAnimationFrame(state.dice.frame);
    state.dice.active = false;
    state.dice.held = false;
    $("DiceOverlay").classList.remove("is-active");
    $("DiceOverlay").setAttribute("aria-hidden", "true");
    $("ConfirmRoll").disabled = !state.pathChoiceId;
    announce("Roll preview complete. Your selections remain unchanged.");
  }

  function skipOrDismissDice() {
    if (!state.dice.active) return;
    if (state.dice.held) {
      dismissDice();
      return;
    }
    cancelAnimationFrame(state.dice.frame);
    enterHeldDice();
  }

  function drawDiceFrame(now) {
    if (!state.dice.active || state.dice.held) return;
    var profile = state.dice.profile;
    var duration = reducedMotion ? 0.01 : profile.anticipation + profile.moving;
    var normalized = Math.min(1, (now - state.dice.startedAt) / (duration * 1000));
    drawDie(normalized, false);
    if (normalized >= 1) {
      enterHeldDice();
      return;
    }
    state.dice.frame = requestAnimationFrame(drawDiceFrame);
  }

  var phi = (1 + Math.sqrt(5)) / 2;
  var icoVertices = [
    [-1, phi, 0], [1, phi, 0], [-1, -phi, 0], [1, -phi, 0],
    [0, -1, phi], [0, 1, phi], [0, -1, -phi], [0, 1, -phi],
    [phi, 0, -1], [phi, 0, 1], [-phi, 0, -1], [-phi, 0, 1]
  ];
  var icoFaces = [
    [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
    [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
    [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
    [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1]
  ];

  function rotateVertex(vertex, ax, ay, az) {
    var x = vertex[0];
    var y = vertex[1];
    var z = vertex[2];
    var cy = Math.cos(ax);
    var sy = Math.sin(ax);
    var y1 = y * cy - z * sy;
    var z1 = y * sy + z * cy;
    var cx = Math.cos(ay);
    var sx = Math.sin(ay);
    var x2 = x * cx + z1 * sx;
    var z2 = -x * sx + z1 * cx;
    var cz = Math.cos(az);
    var sz = Math.sin(az);
    return [x2 * cz - y1 * sz, x2 * sz + y1 * cz, z2];
  }

  function drawDie(normalized, held) {
    var canvas = $("DiceViewport");
    var ctx = canvas.getContext("2d");
    var profile = state.dice.profile || diceProfiles[0];
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    var settle = normalized <= profile.settleStart
      ? 0
      : (normalized - profile.settleStart) / (1 - profile.settleStart);
    settle = settle * settle * (3 - 2 * settle);
    var spin = held ? 0 : profile.turns * Math.PI * 2 * normalized * (1 - settle);
    var ax = held ? -0.34 : spin * 0.72 + 0.35;
    var ay = held ? 0.48 : spin + 0.58;
    var az = held ? -0.08 : spin * 0.31 - 0.22;
    var x = 960 + curveValue(profile.horizontal, normalized) * 1140;
    var y = 500 - curveValue(profile.vertical, normalized) * 720;
    var depth = curveValue(profile.depth, normalized);
    var scale = 116 * curveValue(profile.scale, normalized) * (1 - depth * 0.22);

    var glow = ctx.createRadialGradient(x, y, 18, x, y, held ? 260 : 210);
    glow.addColorStop(0, held ? "rgba(222, 151, 55, 0.38)" : "rgba(148, 54, 35, 0.26)");
    glow.addColorStop(0.48, held ? "rgba(184, 87, 31, 0.13)" : "rgba(108, 39, 30, 0.09)");
    glow.addColorStop(1, "rgba(0, 0, 0, 0)");
    ctx.fillStyle = glow;
    ctx.fillRect(x - 300, y - 300, 600, 600);

    var transformed = icoVertices.map(function (vertex) {
      var rotated = rotateVertex(vertex, ax, ay, az);
      return {
        x: x + rotated[0] * scale,
        y: y - rotated[1] * scale,
        z: rotated[2]
      };
    });
    var faces = icoFaces.map(function (indices, index) {
      return {
        indices: indices,
        index: index,
        z: (
          transformed[indices[0]].z +
          transformed[indices[1]].z +
          transformed[indices[2]].z
        ) / 3
      };
    }).sort(function (a, b) { return a.z - b.z; });

    faces.forEach(function (face) {
      if (face.z < -0.35) return;
      var a = transformed[face.indices[0]];
      var b = transformed[face.indices[1]];
      var c = transformed[face.indices[2]];
      var brightness = Math.max(0, Math.min(1, (face.z + 1.1) / 2.2));
      var red = Math.round(42 + brightness * 70);
      var green = Math.round(12 + brightness * 23);
      var blue = Math.round(13 + brightness * 19);
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(b.x, b.y);
      ctx.lineTo(c.x, c.y);
      ctx.closePath();
      ctx.fillStyle = "rgb(" + red + "," + green + "," + blue + ")";
      ctx.fill();
      ctx.strokeStyle = held
        ? "rgba(240, 193, 104, 0.82)"
        : "rgba(195, 148, 83, 0.62)";
      ctx.lineWidth = held ? 2.2 : 1.5;
      ctx.stroke();
    });

    ctx.beginPath();
    ctx.arc(x, y, held ? 60 : 46, 0, Math.PI * 2);
    ctx.fillStyle = held ? "rgba(47, 13, 12, 0.94)" : "rgba(35, 10, 11, 0.76)";
    ctx.fill();
    ctx.strokeStyle = held ? "#F0D08C" : "rgba(220, 183, 111, 0.78)";
    ctx.lineWidth = 3;
    ctx.stroke();
    ctx.fillStyle = "#F8E7C3";
    ctx.font = (held ? "50px" : "38px") + " Cinzel, Georgia, serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(String(state.dice.result), x, y + 3);
  }

  function applyQueryState() {
    var params = new URLSearchParams(window.location.search);
    var path = params.get("path");
    var choice = (params.get("choice") || "").toUpperCase();
    if ((path === "school" || path === "discipline") && optionFor(path, choice)) {
      selectPathOption(path, choice, false);
    }

    var spent = params.get("spent");
    if (!spent) return;
    spent.split(",").forEach(function (entry) {
      var parts = entry.split(":");
      var stableId = (parts[0] || "").toUpperCase();
      var amount = Math.max(0, Math.floor(Number(parts[1]) || 0));
      if (state.allocations[stableId] !== undefined) {
        state.allocations[stableId] = Math.min(amount, remainingPoints() + (state.allocations[stableId] || 0));
      }
    });
    refreshStats();
  }

  function applyQueryDiceState() {
    var requestedState = new URLSearchParams(window.location.search).get("dice");
    if (!state.pathChoiceId ||
        (requestedState !== "rolling" && requestedState !== "held")) {
      return;
    }
    setTimeout(function () {
      beginDiceRoll();
      if (requestedState === "held") {
        setTimeout(skipOrDismissDice, 80);
      }
    }, 80);
  }

  function initialize() {
    window.addEventListener("resize", fitStage);
    fitStage();
    paintBackdrop();
    renderStats();
    renderOrbit("school", prototypeHostData.spellSchools, "SpellSchoolOptions", "SpellLines");
    renderOrbit(
      "discipline",
      prototypeHostData.combatDisciplines,
      "CombatDisciplineOptions",
      "DisciplineLines"
    );
    applyQueryState();
    applyQueryDiceState();
    loadCanonicalDisciplines();

    $("ResetPoints").addEventListener("click", resetPoints);
    $("ConfirmRoll").addEventListener("click", beginDiceRoll);
    $("DiceViewport").addEventListener("click", skipOrDismissDice);
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && state.dice.active) {
        dismissDice();
        event.preventDefault();
      }
    });

    requestAnimationFrame(function () {
      requestAnimationFrame(function () {
        $("LevelUpLayout").classList.add("is-open");
      });
    });

    window.__arenaLevelUpPrototype = {
      data: prototypeHostData,
      state: state,
      selectPathOption: selectPathOption,
      beginDiceRoll: beginDiceRoll,
      dismissDice: dismissDice
    };
  }

  initialize();
})();
