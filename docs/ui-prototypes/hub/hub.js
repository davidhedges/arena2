/* Browser-only Hub interaction scaffold. Links intentionally demonstrate
 * affordances without implementing the future screens or matchmaking flow. */
(function () {
  "use strict";

  var stage = document.getElementById("Stage");
  var toast = document.getElementById("Toast");
  var toastTimer = 0;

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
    ["NavDisciplines", "Disciplines UI will be designed separately."],
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

  document.getElementById("QueueName").parentElement.parentElement.addEventListener("click", function () {
    showToast("Queue selection will be wired in the game.");
  });

  document.getElementById("FindMatchButton").addEventListener("click", function () {
    var button = this;
    var title = button.querySelector(".button-title");
    var subtitle = button.querySelector(".button-subtitle");
    var searching = button.classList.toggle("is-searching");
    title.textContent = searching ? "SEARCHING…" : "FIND MATCH";
    subtitle.textContent = searching ? "CLICK TO CANCEL" : "RANKED MATCHMAKING";
  });

  window.addEventListener("resize", fitStage);
  fitStage();
}());
