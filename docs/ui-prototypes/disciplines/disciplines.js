/* ARCHIVED IN PLACE 2026-08-26. This legacy primary/secondary and
 * school-as-discipline interaction is retained only as visual history.
 * Do not implement or extend it; see README.md and the combat-build plan.
 *
 * Historical inputs:
 * - discipline roster and kind: server/src/progression_catalog.shared.json
 * - ability ownership: abilities[].discipline_id from the same catalog
 * - ability copy: ABILITY rows in action_presentations[]
 * - stat collection: server/src/progression.rs AllocatedStatTotals
 *
 * This archived prototype deliberately keeps state in memory. It does not call a
 * reducer, persist a build, or imply a server-authoritative mutation path. */
(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var stage = $("Stage");
  var toastTimer = 0;
  var iconRoot = "../../../Assets/Arena/Resources/UI/AbilityIcons/";

  var disciplinePresentation = {
    SUBTLETY: {
      description: "Precision, mobility, and lethal dagger openings.",
      color: "#9f78c2",
      icon: iconRoot + "COMBAT_DISCIPLINE_SWITCH/SUBTLETY.png"
    },
    WAR: {
      description: "Relentless pressure with a greatsword.",
      color: "#d5a148",
      icon: iconRoot + "COMBAT_DISCIPLINE_SWITCH/WAR.png"
    },
    ZEAL: {
      description: "Shielded resolve, sacred force, and protection.",
      color: "#d8b35a",
      icon: iconRoot + "COMBAT_DISCIPLINE_SWITCH/ZEAL.png"
    },
    PRECISION: {
      description: "Measured bow attacks and evasive control.",
      color: "#6f9f69",
      icon: iconRoot + "COMBAT_DISCIPLINE_SWITCH/PRECISION.png"
    },
    BLIGHT: {
      description: "Frost, ice, and the binding force of cold.",
      color: "#6cabd2",
      icon: iconRoot + "ABILITY/SPELL_ICICLE.png"
    },
    MORTALITY: {
      description: "Necromancy, shadow, and corrosive affliction.",
      color: "#799760",
      icon: iconRoot + "ABILITY/SPELL_NECROTIC_AURA.png"
    },
    RUIN: {
      description: "Fire and lightning shaped for destruction.",
      color: "#bd6a4c",
      icon: iconRoot + "ABILITY/SPELL_FIREBALL.png"
    },
    DIVINITY: {
      description: "Holy restoration, protection, and radiant judgment.",
      color: "#d8c78a",
      icon: iconRoot + "ABILITY/SPELL_CELESTIAL_MANTLE.png"
    },
    ARCANA: {
      description: "Pure magic, control, and staff technique.",
      color: "#6f83c4",
      icon: iconRoot + "COMBAT_DISCIPLINE_SWITCH/ARCANA.png"
    },
    PRIMAL: {
      description: "Wind and the unyielding force of the natural world.",
      color: "#69a1a0",
      icon: iconRoot + "ABILITY/SPELL_GUST_OF_WIND.png"
    }
  };

  var fallbackCatalog = {
    combat_disciplines: [
      { discipline_id: "SUBTLETY", display_name: "Subtlety", discipline_kind: "WEAPON", sort_order: 10 },
      { discipline_id: "WAR", display_name: "War", discipline_kind: "WEAPON", sort_order: 20 },
      { discipline_id: "ZEAL", display_name: "Zeal", discipline_kind: "WEAPON", sort_order: 30 },
      { discipline_id: "PRECISION", display_name: "Precision", discipline_kind: "WEAPON", sort_order: 40 },
      { discipline_id: "BLIGHT", display_name: "Blight", discipline_kind: "SPELL_SCHOOL", sort_order: 100 },
      { discipline_id: "MORTALITY", display_name: "Mortality", discipline_kind: "SPELL_SCHOOL", sort_order: 105 },
      { discipline_id: "RUIN", display_name: "Ruin", discipline_kind: "SPELL_SCHOOL", sort_order: 110 },
      { discipline_id: "DIVINITY", display_name: "Divinity", discipline_kind: "SPELL_SCHOOL", sort_order: 120 },
      { discipline_id: "ARCANA", display_name: "Arcana", discipline_kind: "SPELL_SCHOOL", sort_order: 130 },
      { discipline_id: "PRIMAL", display_name: "Primal", discipline_kind: "SPELL_SCHOOL", sort_order: 140 }
    ],
    abilities: [
      ["WAR", "WARRIOR_HEW", "Hew"],
      ["WAR", "WARRIOR_MAIM", "Maim"],
      ["WAR", "WARRIOR_CRUSHING_BLOW", "Crushing Blow"],
      ["WAR", "WARRIOR_CATACLYSM", "Cataclysm"],
      ["WAR", "WARRIOR_BUZZSAW", "Buzzsaw"],
      ["WAR", "WARRIOR_WHIRLWIND", "Whirlwind"],
      ["WAR", "WARRIOR_SUNDER", "Sunder"],
      ["WAR", "WARRIOR_CLEAVE", "Cleave"],
      ["SUBTLETY", "DAGGER_QUICK_CUT", "Quick Cut"],
      ["ZEAL", "PALADIN_SHIELD_PUMMEL", "Shield Bash"],
      ["PRECISION", "ARCHER_POWER_SHOT", "Power Shot"],
      ["BLIGHT", "SPELL_ICICLE", "Icicle"],
      ["MORTALITY", "SPELL_NECROTIC_AURA", "Necrotic Aura"],
      ["RUIN", "SPELL_FIREBALL", "Fireball"],
      ["DIVINITY", "SPELL_RESTORATION", "Restoration"],
      ["ARCANA", "SPELL_MAGIC_MISSILE", "Magic Missile"],
      ["PRIMAL", "SPELL_GUST_OF_WIND", "Gust of Wind"]
    ].map(function (entry, index) {
      return {
        actor_scope: "PLAYER",
        discipline_id: entry[0],
        ability_id: entry[1],
        display_name: entry[2],
        resource_kind: "",
        resource_cost: 0,
        sort_order: index + 1
      };
    }),
    action_presentations: []
  };

  var statDefinitions = [
    { id: "MIGHT", name: "Might", glyph: "M", initial: 6 },
    { id: "INSIGHT", name: "Insight", glyph: "◈", initial: 5 },
    { id: "FINESSE", name: "Finesse", glyph: "⌁", initial: 5 },
    { id: "QUICKNESS", name: "Quickness", glyph: "↯", initial: 4 },
    { id: "FORTITUDE", name: "Fortitude", glyph: "✦", initial: 5 }
  ];

  var data = {
    disciplines: [],
    abilitiesByDiscipline: {},
    presentationsByAbility: {}
  };

  var state = {
    primaryId: "WAR",
    secondaryIds: ["SUBTLETY", "RUIN"],
    selectedAbilities: {},
    stats: {},
    pointsBudget: 25,
    catalogIsLive: false
  };

  statDefinitions.forEach(function (definition) {
    state.stats[definition.id] = definition.initial;
  });

  function make(tag, className, text) {
    var element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }

  function sorted(collection, idKey) {
    return collection.slice().sort(function (a, b) {
      return (a.sort_order || 0) - (b.sort_order || 0) ||
        String(a[idKey] || "").localeCompare(String(b[idKey] || ""));
    });
  }

  function configureCatalog(catalog, isLive) {
    var descriptions = {};
    (catalog.action_presentations || []).forEach(function (entry) {
      if (entry.presentation_kind === "ABILITY") {
        descriptions[entry.presentation_id] = entry.description || "";
      }
    });

    data.disciplines = sorted(catalog.combat_disciplines || [], "discipline_id")
      .map(function (entry) {
        var presentation = disciplinePresentation[entry.discipline_id] || {};
        return {
          id: entry.discipline_id,
          name: entry.display_name,
          kind: entry.discipline_kind,
          sortOrder: entry.sort_order,
          description: presentation.description || "A canonical combat discipline.",
          color: presentation.color || "#d9b56a",
          icon: presentation.icon || iconRoot + "COMBAT_DISCIPLINE_SWITCH/WAR.png"
        };
      });

    data.abilitiesByDiscipline = {};
    data.disciplines.forEach(function (discipline) {
      data.abilitiesByDiscipline[discipline.id] = [];
    });

    sorted(catalog.abilities || [], "ability_id").forEach(function (entry) {
      if (entry.actor_scope !== "PLAYER" || !data.abilitiesByDiscipline[entry.discipline_id]) return;
      data.abilitiesByDiscipline[entry.discipline_id].push({
        id: entry.ability_id,
        name: entry.display_name,
        resource: entry.resource_kind || "",
        cost: entry.resource_cost,
        sortOrder: entry.sort_order,
        description: descriptions[entry.ability_id] || "Select this ability for your provisional discipline loadout."
      });
    });

    data.presentationsByAbility = descriptions;
    state.catalogIsLive = isLive;
    ensureInitialSelections();
    render();
  }

  function selectedSet(disciplineId) {
    if (!state.selectedAbilities[disciplineId]) {
      state.selectedAbilities[disciplineId] = new Set();
    }
    return state.selectedAbilities[disciplineId];
  }

  function ensureInitialSelections() {
    data.disciplines.forEach(function (discipline) {
      selectedSet(discipline.id);
    });

    if (selectedSet("WAR").size === 0) {
      (data.abilitiesByDiscipline.WAR || []).slice(0, 8).forEach(function (ability) {
        selectedSet("WAR").add(ability.id);
      });
    }
    if (selectedSet("SUBTLETY").size === 0 && (data.abilitiesByDiscipline.SUBTLETY || []).length) {
      selectedSet("SUBTLETY").add(data.abilitiesByDiscipline.SUBTLETY[0].id);
    }
    if (selectedSet("RUIN").size === 0 && (data.abilitiesByDiscipline.RUIN || []).length) {
      selectedSet("RUIN").add(data.abilitiesByDiscipline.RUIN[0].id);
    }
  }

  function disciplineFor(id) {
    return data.disciplines.find(function (discipline) { return discipline.id === id; });
  }

  function primaryEligibleDisciplines() {
    return data.disciplines.filter(function (discipline) {
      return (data.abilitiesByDiscipline[discipline.id] || []).length > 0;
    });
  }

  function selectedCount(disciplineId) {
    return selectedSet(disciplineId).size;
  }

  function totalSecondaryAbilities() {
    return state.secondaryIds.reduce(function (total, disciplineId) {
      return total + selectedCount(disciplineId);
    }, 0);
  }

  function allocatedPoints() {
    return statDefinitions.reduce(function (total, definition) {
      return total + state.stats[definition.id];
    }, 0);
  }

  function remainingPoints() {
    return state.pointsBudget - allocatedPoints();
  }

  function validation() {
    var primaryCount = selectedCount(state.primaryId);
    var incompleteSecondaries = state.secondaryIds.filter(function (disciplineId) {
      return selectedCount(disciplineId) < 1;
    });
    return {
      primaryCount: primaryCount,
      primaryValid: primaryCount >= 8,
      incompleteSecondaries: incompleteSecondaries,
      secondariesValid: incompleteSecondaries.length === 0,
      valid: primaryCount >= 8 && incompleteSecondaries.length === 0 && state.secondaryIds.length <= 2
    };
  }

  function imageForAbility(ability, discipline) {
    return iconRoot + "ABILITY/" + ability.id + ".png";
  }

  function assignImageFallback(image, discipline) {
    image.addEventListener("error", function () {
      if (image.src.indexOf(discipline.icon) !== -1) return;
      image.src = discipline.icon;
    }, { once: true });
  }

  function renderPrimaryPicker() {
    var discipline = disciplineFor(state.primaryId);
    if (!discipline) return;
    $("PrimaryIcon").src = discipline.icon;
    $("PrimaryName").textContent = discipline.name.toUpperCase();
    $("PrimaryDescription").textContent = discipline.description;
    $("PrimaryCard").style.borderColor = discipline.color;
  }

  function renderDisciplineGrid() {
    var grid = $("DisciplineGrid");
    grid.replaceChildren();

    data.disciplines.filter(function (discipline) {
      return discipline.id !== state.primaryId;
    }).forEach(function (discipline) {
      var selected = state.secondaryIds.indexOf(discipline.id) !== -1;
      var atLimit = state.secondaryIds.length >= 2 && !selected;
      var button = make("button", "discipline-option" + (selected ? " is-selected" : "") + (atLimit ? " is-disabled" : ""));
      button.type = "button";
      button.style.setProperty("--discipline-color", discipline.color);
      button.setAttribute("aria-pressed", selected ? "true" : "false");
      button.setAttribute("aria-label", (selected ? "Remove " : "Add ") + discipline.name + " secondary discipline");

      var icon = make("span", "discipline-option-icon");
      var image = make("img");
      image.src = discipline.icon;
      image.alt = "";
      icon.appendChild(image);
      button.appendChild(icon);
      button.appendChild(make("span", "discipline-option-name", discipline.name.toUpperCase()));
      button.appendChild(make("span", "discipline-check", "✓"));
      button.addEventListener("click", function () { toggleSecondary(discipline.id); });
      grid.appendChild(button);
    });

    $("SecondaryCount").textContent = state.secondaryIds.length;
  }

  function renderStats() {
    var list = $("StatList");
    list.replaceChildren();
    var remaining = remainingPoints();

    statDefinitions.forEach(function (definition) {
      var row = make("div", "stat-row");
      row.appendChild(make("div", "stat-glyph", definition.glyph));
      row.appendChild(make("div", "stat-name", definition.name.toUpperCase()));

      var minus = make("button", "stat-step", "−");
      minus.type = "button";
      minus.disabled = state.stats[definition.id] <= 0;
      minus.setAttribute("aria-label", "Remove one point from " + definition.name);
      minus.addEventListener("click", function () { changeStat(definition.id, -1); });
      row.appendChild(minus);

      row.appendChild(make("div", "stat-value", state.stats[definition.id]));

      var plus = make("button", "stat-step", "+");
      plus.type = "button";
      plus.disabled = remaining <= 0;
      plus.setAttribute("aria-label", "Add one point to " + definition.name);
      plus.addEventListener("click", function () { changeStat(definition.id, 1); });
      row.appendChild(plus);
      list.appendChild(row);
    });

    var allocated = allocatedPoints();
    $("PointsRemaining").textContent = remaining;
    $("PointsAllocated").textContent = allocated;
    $("PointsBudget").textContent = state.pointsBudget;
    $("PointRuleFill").style.width = Math.round((allocated / state.pointsBudget) * 100) + "%";
  }

  function buildAbilityTile(ability, discipline, compact) {
    var selected = selectedSet(discipline.id).has(ability.id);
    var button = make("button", "ability-tile" + (selected ? " is-selected" : ""));
    button.type = "button";
    button.setAttribute("aria-pressed", selected ? "true" : "false");
    button.setAttribute("aria-label", (selected ? "Remove " : "Select ") + ability.name);

    var art = make("span", "ability-art");
    var image = make("img");
    image.src = imageForAbility(ability, discipline);
    image.alt = "";
    assignImageFallback(image, discipline);
    art.appendChild(image);
    button.appendChild(art);
    button.appendChild(make("span", "ability-name", ability.name));
    button.appendChild(make("span", "ability-check", "✓"));

    button.addEventListener("click", function () {
      toggleAbility(discipline.id, ability.id);
    });
    button.addEventListener("pointerenter", function (event) {
      showAbilityTooltip(event, ability, discipline);
    });
    button.addEventListener("pointermove", positionAbilityTooltip);
    button.addEventListener("pointerleave", hideAbilityTooltip);
    button.addEventListener("focus", function () {
      var rect = button.getBoundingClientRect();
      showAbilityTooltip({ clientX: rect.right, clientY: rect.top }, ability, discipline);
    });
    button.addEventListener("blur", hideAbilityTooltip);
    return button;
  }

  function renderPrimaryAbilities() {
    var discipline = disciplineFor(state.primaryId);
    var abilities = data.abilitiesByDiscipline[state.primaryId] || [];
    var grid = $("PrimaryAbilityGrid");
    grid.replaceChildren();
    discipline && abilities.forEach(function (ability) {
      grid.appendChild(buildAbilityTile(ability, discipline, false));
    });

    var count = selectedCount(state.primaryId);
    $("PrimaryAbilityDiscipline").textContent = discipline ? discipline.name.toUpperCase() : state.primaryId;
    $("PrimaryAbilityCounter").innerHTML = count + " <span>/ 8 MIN</span>";
    $("PrimaryAbilityCounter").classList.toggle("is-incomplete", count < 8);
  }

  function renderSecondaryAbilities() {
    var groups = $("SecondaryAbilityGroups");
    groups.replaceChildren();

    if (state.secondaryIds.length === 0) {
      var empty = make("div", "secondary-empty");
      empty.appendChild(make("strong", "", "NO SECONDARY DISCIPLINES ACTIVE"));
      empty.appendChild(make("span", "", "Choose up to two supporting paths from the left panel."));
      groups.appendChild(empty);
    }

    state.secondaryIds.forEach(function (disciplineId) {
      var discipline = disciplineFor(disciplineId);
      if (!discipline) return;
      var group = make("section", "secondary-group");
      group.dataset.disciplineId = disciplineId;
      group.style.setProperty("--secondary-color", discipline.color);

      var heading = make("header", "secondary-group-heading");
      var icon = make("span", "secondary-group-icon");
      var image = make("img");
      image.src = discipline.icon;
      image.alt = "";
      icon.appendChild(image);
      heading.appendChild(icon);
      heading.appendChild(make("span", "secondary-group-name", discipline.name.toUpperCase()));
      var count = selectedCount(disciplineId);
      var countCopy = make("span", "secondary-group-count" + (count < 1 ? " is-incomplete" : ""));
      countCopy.innerHTML = "<strong>" + count + "</strong> / 1 MIN";
      heading.appendChild(countCopy);
      group.appendChild(heading);

      var grid = make("div", "ability-grid ability-grid--secondary");
      (data.abilitiesByDiscipline[disciplineId] || []).forEach(function (ability) {
        grid.appendChild(buildAbilityTile(ability, discipline, true));
      });
      group.appendChild(grid);
      groups.appendChild(group);
    });

    $("SecondaryAbilityCounter").innerHTML = totalSecondaryAbilities() + " <span>SELECTED</span>";
  }

  function summarySecondaryRow(discipline) {
    var row = make("div", "summary-secondary-row");
    row.style.setProperty("--secondary-color", discipline.color);
    var icon = make("span", "summary-secondary-icon");
    var image = make("img");
    image.src = discipline.icon;
    image.alt = "";
    icon.appendChild(image);
    row.appendChild(icon);
    row.appendChild(make("span", "summary-secondary-name", discipline.name.toUpperCase()));
    row.appendChild(make("span", "summary-secondary-abilities", selectedCount(discipline.id) + " abilities"));
    return row;
  }

  function requirementRow(complete, copy) {
    var row = make("div", "requirement-row" + (complete ? "" : " is-incomplete"));
    row.appendChild(make("span", "requirement-icon", complete ? "✓" : "!"));
    row.appendChild(make("span", "", copy));
    return row;
  }

  function renderSummary() {
    var discipline = disciplineFor(state.primaryId);
    var check = validation();
    if (!discipline) return;

    $("SummaryPrimaryIcon").src = discipline.icon;
    $("SummaryPrimaryName").textContent = discipline.name.toUpperCase();
    $("SummaryPrimaryKind").textContent = discipline.kind === "WEAPON" ? "WEAPON DISCIPLINE" : "SPELL-SCHOOL DISCIPLINE";
    $("SummaryPrimaryEmblem").style.borderColor = discipline.color;

    var secondaryList = $("SummarySecondaryList");
    secondaryList.replaceChildren();
    if (state.secondaryIds.length === 0) {
      secondaryList.appendChild(make("div", "summary-secondary-empty", "No supporting discipline selected"));
    } else {
      state.secondaryIds.forEach(function (id) {
        var secondary = disciplineFor(id);
        if (secondary) secondaryList.appendChild(summarySecondaryRow(secondary));
      });
    }

    var requirements = $("RequirementList");
    requirements.replaceChildren();
    requirements.appendChild(requirementRow(
      check.primaryValid,
      discipline.name + " primary abilities: " + check.primaryCount + " / 8"
    ));

    var metSecondaries = state.secondaryIds.length - check.incompleteSecondaries.length;
    requirements.appendChild(requirementRow(
      check.secondariesValid,
      state.secondaryIds.length === 0
        ? "Secondary disciplines are optional"
        : "Secondary minima met: " + metSecondaries + " / " + state.secondaryIds.length
    ));
    requirements.appendChild(requirementRow(true, "Secondary disciplines: " + state.secondaryIds.length + " / 2 maximum"));

    $("SummaryPrimaryAbilities").textContent = check.primaryCount;
    $("SummarySecondaryAbilities").textContent = totalSecondaryAbilities();
    $("SummaryDisciplines").textContent = (1 + state.secondaryIds.length) + " / 3";
    $("SummaryPoints").textContent = allocatedPoints() + " / " + state.pointsBudget;

    var status = $("SaveStatus");
    var statusCopy = $("SaveStatusCopy");
    status.classList.toggle("is-incomplete", !check.valid);
    $("SaveStatusIcon").textContent = check.valid ? "✓" : "!";
    if (!check.primaryValid) {
      statusCopy.textContent = "Select " + (8 - check.primaryCount) + " more " + discipline.name + " abilities";
    } else if (!check.secondariesValid) {
      statusCopy.textContent = "Every secondary needs at least one ability";
    } else if (remainingPoints() > 0) {
      statusCopy.textContent = "Requirements met · " + remainingPoints() + " points unspent";
    } else {
      statusCopy.textContent = "All requirements met";
    }
    $("SaveLoadout").disabled = !check.valid;
  }

  function renderCatalogSource() {
    var source = document.querySelector(".catalog-source");
    var dot = document.querySelector(".source-dot");
    source.lastChild.nodeValue = state.catalogIsLive ? " LIVE PROGRESSION CATALOG" : " EMBEDDED CATALOG FALLBACK";
    dot.style.backgroundColor = state.catalogIsLive ? "#7f9f63" : "#c58f2a";
  }

  function scrollState() {
    var secondary = {};
    document.querySelectorAll(".secondary-group").forEach(function (group) {
      var grid = group.querySelector(".ability-grid--secondary");
      secondary[group.dataset.disciplineId] = grid ? grid.scrollTop : 0;
    });
    return {
      primary: $("PrimaryAbilityGrid") ? $("PrimaryAbilityGrid").scrollTop : 0,
      secondary: secondary
    };
  }

  function restoreScroll(saved) {
    $("PrimaryAbilityGrid").scrollTop = saved.primary;
    document.querySelectorAll(".secondary-group").forEach(function (group) {
      var grid = group.querySelector(".ability-grid--secondary");
      if (grid) grid.scrollTop = saved.secondary[group.dataset.disciplineId] || 0;
    });
  }

  function render() {
    var savedScroll = scrollState();
    renderPrimaryPicker();
    renderDisciplineGrid();
    renderStats();
    renderPrimaryAbilities();
    renderSecondaryAbilities();
    renderSummary();
    renderCatalogSource();
    restoreScroll(savedScroll);
  }

  function cyclePrimary(direction) {
    var eligible = primaryEligibleDisciplines();
    if (eligible.length === 0) {
      showToast("No discipline abilities are currently available.");
      return;
    }
    var index = eligible.findIndex(function (discipline) { return discipline.id === state.primaryId; });
    index = index < 0 ? 0 : (index + direction + eligible.length) % eligible.length;
    var next = eligible[index];
    if (state.secondaryIds.indexOf(next.id) !== -1) {
      state.secondaryIds = state.secondaryIds.filter(function (id) { return id !== next.id; });
    }
    state.primaryId = next.id;
    hideAbilityTooltip();
    render();
  }

  function toggleSecondary(disciplineId) {
    var index = state.secondaryIds.indexOf(disciplineId);
    if (index !== -1) {
      state.secondaryIds.splice(index, 1);
      $("SecondaryHelp").textContent = "Choose up to two. Each active secondary requires at least one ability.";
      $("SecondaryHelp").classList.remove("is-warning");
      render();
      return;
    }
    if (state.secondaryIds.length >= 2) {
      $("SecondaryHelp").textContent = "Two secondary disciplines are already active. Remove one to change it.";
      $("SecondaryHelp").classList.add("is-warning");
      showToast("Maximum of two secondary disciplines.");
      return;
    }
    state.secondaryIds.push(disciplineId);
    $("SecondaryHelp").textContent = "Choose at least one ability from the newly active discipline.";
    $("SecondaryHelp").classList.toggle("is-warning", selectedCount(disciplineId) < 1);
    render();
  }

  function toggleAbility(disciplineId, abilityId) {
    var selected = selectedSet(disciplineId);
    if (selected.has(abilityId)) selected.delete(abilityId);
    else selected.add(abilityId);
    render();
  }

  function changeStat(statId, amount) {
    if (amount > 0 && remainingPoints() <= 0) return;
    state.stats[statId] = Math.max(0, state.stats[statId] + amount);
    renderStats();
    renderSummary();
  }

  function resetPoints() {
    statDefinitions.forEach(function (definition) { state.stats[definition.id] = 0; });
    renderStats();
    renderSummary();
    showToast("Ability point allocations reset. 25 points remain available.");
  }

  function showToast(message) {
    var toast = $("Toast");
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.classList.add("is-visible");
    toastTimer = window.setTimeout(function () {
      toast.classList.remove("is-visible");
    }, 2300);
  }

  function showAbilityTooltip(event, ability, discipline) {
    var tooltip = $("AbilityTooltip");
    tooltip.replaceChildren();
    tooltip.appendChild(make("div", "tooltip-name", ability.name.toUpperCase()));
    var resourceCopy = ability.resource
      ? ability.resource + (ability.cost !== null && ability.cost !== undefined ? " · " + ability.cost + " COST" : "")
      : discipline.name + " ABILITY";
    tooltip.appendChild(make("div", "tooltip-meta", resourceCopy));
    tooltip.appendChild(make("div", "tooltip-description", ability.description));
    tooltip.setAttribute("aria-hidden", "false");
    tooltip.classList.add("is-visible");
    positionAbilityTooltip(event);
  }

  function positionAbilityTooltip(event) {
    var tooltip = $("AbilityTooltip");
    if (!tooltip.classList.contains("is-visible")) return;
    var rect = stage.getBoundingClientRect();
    var scaleX = 1920 / rect.width;
    var scaleY = 1080 / rect.height;
    var x = (event.clientX - rect.left) * scaleX + 18;
    var y = (event.clientY - rect.top) * scaleY + 18;
    x = Math.min(1920 - 290, Math.max(16, x));
    y = Math.min(1080 - 150, Math.max(108, y));
    tooltip.style.left = x + "px";
    tooltip.style.top = y + "px";
  }

  function hideAbilityTooltip() {
    var tooltip = $("AbilityTooltip");
    tooltip.classList.remove("is-visible");
    tooltip.setAttribute("aria-hidden", "true");
  }

  function fitStage() {
    var wrap = stage.parentElement;
    var scale = Math.min(wrap.clientWidth / 1920, wrap.clientHeight / 1080);
    stage.style.transform = "scale(" + scale + ")";
  }

  function bindStaticInteractions() {
    $("PreviousPrimary").addEventListener("click", function () { cyclePrimary(-1); });
    $("NextPrimary").addEventListener("click", function () { cyclePrimary(1); });
    $("ResetPoints").addEventListener("click", resetPoints);
    $("SaveLoadout").addEventListener("click", function () {
      if (!validation().valid) return;
      showToast("Loadout validated. Save is a presentation preview only; no progression was mutated.");
    });
    document.querySelectorAll(".pending-link").forEach(function (button) {
      button.addEventListener("click", function () {
        showToast(button.dataset.message || "This destination is outside the current screen slice.");
      });
    });
    window.addEventListener("resize", fitStage);
  }

  function loadCanonicalCatalog() {
    fetch("../../../server/src/progression_catalog.shared.json", { cache: "no-store" })
      .then(function (response) {
        if (!response.ok) throw new Error("catalog request failed");
        return response.json();
      })
      .then(function (catalog) {
        configureCatalog(catalog, true);
      })
      .catch(function () {
        configureCatalog(fallbackCatalog, false);
      });
  }

  bindStaticInteractions();
  configureCatalog(fallbackCatalog, false);
  fitStage();
  loadCanonicalCatalog();
}());
