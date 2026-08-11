/* Browser-only Hub interaction scaffold. Links intentionally demonstrate
 * affordances without implementing the future screens or matchmaking flow. */
(function () {
  "use strict";

  var stage = document.getElementById("Stage");
  var toast = document.getElementById("Toast");
  var toastTimer = 0;
  var queueMode = "RANKED";
  var selectedFormat = "3v3";
  var searching = false;
  var lastFocusedElement = null;

  var queueToggle = document.getElementById("QueueToggle");
  var queueName = document.getElementById("QueueName");
  var findMatchButton = document.getElementById("FindMatchButton");
  var matchOverlay = document.getElementById("MatchOverlay");
  var formatDialog = matchOverlay.querySelector(".format-dialog");
  var dialogQueueName = document.getElementById("DialogQueueName");
  var selectionValue = document.getElementById("SelectionValue");
  var queueConfirmSubtitle = document.getElementById("QueueConfirmSubtitle");
  var formatOptions = Array.prototype.slice.call(document.querySelectorAll(".format-option"));

  function fitStage() {
    var wrap = stage.parentElement;
    var scale = Math.min(wrap.clientWidth / 1920, wrap.clientHeight / 1080);
    stage.style.transform = "scale(" + scale + ")";
  }

  function showToast(message) {
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.classList.add("is-visible");
    toastTimer = window.setTimeout(function () {
      toast.classList.remove("is-visible");
    }, 1800);
  }

  var pendingLinks = [
    ["NavEquipment", "Equipment UI will be designed separately."],
    ["NavAppearance", "Appearance UI will be designed separately."],
    ["NavCareer", "Career UI will be designed separately."],
    ["NavStore", "Store UI will be designed separately."],
    ["SocialButton", "Social UI is not part of this Hub slice."],
    ["InboxButton", "Inbox UI is not part of this Hub slice."],
    ["SettingsButton", "Settings UI is not part of this Hub slice."],
    ["ExitButton", "Exit action is disabled in the browser spec."],
    ["PartyAddButton", "Party invitations will be wired in the game."],
    ["PracticeButton", "Practice flow will be wired in the game."]
  ];

  pendingLinks.forEach(function (entry) {
    document.getElementById(entry[0]).addEventListener("click", function () {
      showToast(entry[1]);
    });
  });

  document.getElementById("NavPlay").addEventListener("click", function () {
    showToast("You are already on Play.");
  });

  document.getElementById("NavDisciplines").addEventListener("click", function () {
    window.location.href = "../disciplines/";
  });

  function titleCase(value) {
    return value.charAt(0) + value.slice(1).toLowerCase();
  }

  function updateSelectionCopy() {
    var formatLabel = selectedFormat.toUpperCase();
    dialogQueueName.textContent = titleCase(queueMode);
    selectionValue.textContent = queueMode + " · " + formatLabel;
    queueConfirmSubtitle.textContent = "FIND A " + queueMode + " " + formatLabel + " MATCH";
  }

  function updateMatchButton() {
    var title = findMatchButton.querySelector(".button-title");
    var subtitle = findMatchButton.querySelector(".button-subtitle");

    findMatchButton.classList.toggle("is-searching", searching);
    title.textContent = searching ? "SEARCHING " + selectedFormat.toUpperCase() + "…" : "FIND MATCH";
    subtitle.textContent = searching ? queueMode + " · CLICK TO CANCEL" : queueMode + " MATCHMAKING";
  }

  function selectFormat(format, moveFocus) {
    selectedFormat = format;
    formatOptions.forEach(function (option) {
      var isSelected = option.getAttribute("data-format") === selectedFormat;
      option.classList.toggle("is-selected", isSelected);
      option.setAttribute("aria-checked", isSelected ? "true" : "false");
      option.tabIndex = isSelected ? 0 : -1;
      if (isSelected && moveFocus) {
        option.focus();
      }
    });
    updateSelectionCopy();
  }

  function openOverlay() {
    lastFocusedElement = document.activeElement;
    updateSelectionCopy();
    matchOverlay.classList.add("is-open");
    matchOverlay.setAttribute("aria-hidden", "false");
    findMatchButton.setAttribute("aria-expanded", "true");

    var selectedOption = matchOverlay.querySelector(".format-option.is-selected");
    if (selectedOption) {
      selectedOption.focus();
    }
  }

  function closeOverlay(restoreFocus) {
    matchOverlay.classList.remove("is-open");
    matchOverlay.setAttribute("aria-hidden", "true");
    findMatchButton.setAttribute("aria-expanded", "false");

    if (restoreFocus && lastFocusedElement) {
      lastFocusedElement.focus();
    }
  }

  queueToggle.addEventListener("click", function () {
    if (searching) {
      showToast("Cancel your current search before switching queues.");
      return;
    }

    queueMode = queueMode === "RANKED" ? "CASUAL" : "RANKED";
    queueName.textContent = queueMode;
    queueToggle.setAttribute(
      "aria-label",
      "Switch from " + titleCase(queueMode) + " to " + (queueMode === "RANKED" ? "Casual" : "Ranked") + " matchmaking"
    );
    updateSelectionCopy();
    updateMatchButton();
  });

  formatOptions.forEach(function (option, index) {
    option.addEventListener("click", function () {
      selectFormat(option.getAttribute("data-format"), false);
    });

    option.addEventListener("keydown", function (event) {
      var nextIndex = index;

      if (event.key === "ArrowRight" || event.key === "ArrowDown") {
        nextIndex = (index + 1) % formatOptions.length;
      } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
        nextIndex = (index - 1 + formatOptions.length) % formatOptions.length;
      } else if (event.key === "Home") {
        nextIndex = 0;
      } else if (event.key === "End") {
        nextIndex = formatOptions.length - 1;
      } else {
        return;
      }

      event.preventDefault();
      selectFormat(formatOptions[nextIndex].getAttribute("data-format"), true);
    });
  });

  findMatchButton.addEventListener("click", function () {
    if (searching) {
      searching = false;
      updateMatchButton();
      showToast("Match search cancelled.");
      return;
    }

    openOverlay();
  });

  document.getElementById("QueueConfirm").addEventListener("click", function () {
    searching = true;
    updateMatchButton();
    closeOverlay(true);
    showToast(queueMode + " " + selectedFormat.toUpperCase() + " search started.");
  });

  document.getElementById("DialogClose").addEventListener("click", function () {
    closeOverlay(true);
  });

  document.getElementById("OverlayScrim").addEventListener("click", function () {
    closeOverlay(true);
  });

  document.addEventListener("keydown", function (event) {
    if (!matchOverlay.classList.contains("is-open")) {
      return;
    }

    if (event.key === "Escape") {
      closeOverlay(true);
      return;
    }

    if (event.key === "Tab") {
      var focusableElements = Array.prototype.slice.call(formatDialog.querySelectorAll("button:not([disabled])"));
      var firstElement = focusableElements[0];
      var lastElement = focusableElements[focusableElements.length - 1];

      if (event.shiftKey && document.activeElement === firstElement) {
        event.preventDefault();
        lastElement.focus();
      } else if (!event.shiftKey && document.activeElement === lastElement) {
        event.preventDefault();
        firstElement.focus();
      }
    }
  });

  window.addEventListener("resize", fitStage);
  selectFormat(selectedFormat, false);
  updateMatchButton();
  fitStage();
}());
