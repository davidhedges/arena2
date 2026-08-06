/* Arena Equipment browser interaction scaffold. State is local and does not
 * call reducers or imply that an armor set has been persisted. */
(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var sets = [
    {
      id: "PEASANT", tier: "LIGHT", name: "PEASANT ATTIRE", shortName: "Peasant Attire",
      glyph: "◇", pieces: 4, headline: "UNRESTRICTED MOBILITY",
      flavor: "Simple clothing that leaves movement and spellcasting completely unimpeded.",
      physical: 0, magical: 0, move: 0, cast: 0, equipped: false
    },
    {
      id: "APPRENTICE", tier: "LIGHT", name: "APPRENTICE VESTMENTS", shortName: "Apprentice Vestments",
      glyph: "✧", pieces: 7, headline: "UNRESTRICTED MOBILITY",
      flavor: "Cloth vestments for combatants who value speed and unhindered spellwork.",
      physical: 0, magical: 0, move: 0, cast: 0, equipped: false
    },
    {
      id: "LEATHER", tier: "MEDIUM", name: "RANGER LEATHERS", shortName: "Ranger Leathers",
      glyph: "◆", pieces: 7, headline: "BALANCED PROTECTION",
      flavor: "Supple layered leather that balances reliable protection with full mobility.",
      physical: 20, magical: 20, move: 0, cast: 0, equipped: false
    },
    {
      id: "IRON", tier: "HEAVY", name: "IRON WARPLATE", shortName: "Iron Warplate",
      glyph: "⬟", pieces: 7, headline: "MAXIMUM PROTECTION",
      flavor: "Battle-worn iron plate built to absorb punishing blows and hostile magic.",
      physical: 40, magical: 40, move: -10, cast: -20, equipped: false
    },
    {
      id: "GILDED", tier: "HEAVY", name: "GILDED WARPLATE", shortName: "Gilded Warplate",
      glyph: "⬢", pieces: 7, headline: "MAXIMUM PROTECTION",
      flavor: "Ornate plate built to absorb punishing blows and hostile magic.",
      physical: 40, magical: 40, move: -10, cast: -20, equipped: true
    }
  ];

  var state = { tier: "HEAVY", selectedId: "GILDED" };
  var toastTimer = 0;

  function selectedSet() {
    return sets.find(function (set) { return set.id === state.selectedId; }) || sets[0];
  }

  function filteredSets() {
    return sets.filter(function (set) { return set.tier === state.tier; });
  }

  function formatPercent(value, positivePrefix) {
    if (value === 0) return "0%";
    if (value > 0) return (positivePrefix ? "+" : "") + value + "%";
    return "−" + Math.abs(value) + "%";
  }

  function showToast(message) {
    var toast = $("Toast");
    toast.textContent = message;
    toast.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () { toast.classList.remove("is-visible"); }, 2200);
  }

  function renderTierButtons() {
    document.querySelectorAll(".tier-button").forEach(function (button) {
      var selected = button.getAttribute("data-tier") === state.tier;
      button.classList.toggle("is-selected", selected);
      button.setAttribute("aria-pressed", selected ? "true" : "false");
    });
  }

  function renderSetList() {
    var visibleSets = filteredSets();
    var list = $("SetList");
    list.innerHTML = "";
    $("SetCount").textContent = visibleSets.length + (visibleSets.length === 1 ? " SET" : " SETS");

    visibleSets.forEach(function (set) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "set-card" + (set.id === state.selectedId ? " is-selected" : "");
      button.setAttribute("data-set", set.id);
      button.setAttribute("aria-pressed", set.id === state.selectedId ? "true" : "false");
      button.innerHTML =
        '<span class="set-sigil"><span>' + set.glyph + '</span></span>' +
        '<span class="set-copy"><small>' + set.tier + ' ARMOR · ' + set.pieces + ' PIECES</small>' +
        '<strong>' + set.name + '</strong><em>' + set.physical + '% physical · ' + set.magical + '% magical</em></span>' +
        '<span class="set-check">' + (set.equipped ? "✓" : "›") + '</span>';
      button.addEventListener("click", function () {
        state.selectedId = set.id;
        render();
      });
      list.appendChild(button);
    });
  }

  function renderDetails() {
    var set = selectedSet();
    $("ShowcaseTier").textContent = set.tier + " ARMOR";
    $("ShowcaseName").textContent = set.name;
    $("DetailsName").textContent = set.name;
    $("DetailsFlavor").textContent = set.flavor;
    $("SummaryGlyph").textContent = set.glyph;
    $("SummaryTier").textContent = set.tier + " ARMOR";
    $("SummaryHeadline").textContent = set.headline;
    $("PhysicalResistance").textContent = formatPercent(set.physical, true);
    $("MagicalResistance").textContent = formatPercent(set.magical, true);
    $("MoveSpeed").textContent = formatPercent(set.move, false);
    $("CastSpeed").textContent = formatPercent(set.cast, false);
    $("PieceCount").textContent = set.pieces + " / " + set.pieces + " PIECES";

    $("MoveSpeedRow").classList.toggle("is-hidden", set.move === 0);
    $("CastSpeedRow").classList.toggle("is-hidden", set.cast === 0);
    $("NoTradeoffs").classList.toggle("is-visible", set.move === 0 && set.cast === 0);
    $("EquippedChip").classList.toggle("is-hidden", !set.equipped);

    var equip = $("EquipButton");
    equip.classList.toggle("is-equipped", set.equipped);
    equip.innerHTML = set.equipped ? "◆&nbsp;&nbsp; EQUIPPED &nbsp;&nbsp;◆" : "◆&nbsp;&nbsp; EQUIP COMPLETE SET &nbsp;&nbsp;◆";
  }

  function render() {
    renderTierButtons();
    renderSetList();
    renderDetails();
  }

  function cycleSet(direction) {
    var visible = filteredSets();
    var index = visible.findIndex(function (set) { return set.id === state.selectedId; });
    if (index < 0) index = 0;
    index = (index + direction + visible.length) % visible.length;
    state.selectedId = visible[index].id;
    render();
  }

  document.querySelectorAll(".tier-button").forEach(function (button) {
    button.addEventListener("click", function () {
      state.tier = button.getAttribute("data-tier") || "LIGHT";
      state.selectedId = filteredSets()[0].id;
      render();
    });
  });

  document.querySelectorAll(".pending-link, .tool-button, .back-button").forEach(function (button) {
    button.addEventListener("click", function () {
      showToast(button.getAttribute("data-message") || "Prototype only — navigation is not wired.");
    });
  });

  $("PreviousSet").addEventListener("click", function () { cycleSet(-1); });
  $("NextSet").addEventListener("click", function () { cycleSet(1); });
  $("EquipButton").addEventListener("click", function () {
    var set = selectedSet();
    sets.forEach(function (candidate) { candidate.equipped = candidate.id === set.id; });
    showToast(set.shortName + " equipped as a complete set.");
    render();
  });

  function resizeStage() {
    var stage = $("Stage");
    var wrap = stage.parentElement;
    var scale = Math.min(wrap.clientWidth / 1920, wrap.clientHeight / 1080);
    stage.style.transform = "scale(" + scale + ")";
  }

  window.addEventListener("resize", resizeStage);
  resizeStage();
  render();
}());
