#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Arena.Match;
using Arena.Network;
using SpacetimeDB.ClientApi;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.UIElements;

namespace Arena.UI
{
    /// <summary>
    /// Intermission-only survival shop. Layout and styling are translated from
    /// docs/ui-prototypes/survival-shop; this controller only binds server rows.
    /// </summary>
    public sealed class SurvivalShopScreen : MonoBehaviour
    {
        private const string OpenClass = "is-open";
        private const string PurchasedClass = "is-purchased";
        private const string IntermissionPhase = "INTERMISSION";

        private static readonly IReadOnlyDictionary<string, ModifierCopy> ModifierCopies =
            new Dictionary<string, ModifierCopy>(StringComparer.Ordinal)
            {
                ["PHYSICAL_DAMAGE"] = new("PHYSICAL DAMAGE", "+8% physical damage"),
                ["FORTITUDE"] = new("FORTITUDE", "+12 max HP"),
                ["MOVE_SPEED"] = new("MOVE SPEED", "+4% movement speed"),
                ["CRIT_CHANCE"] = new("CRITICAL CHANCE", "+3% critical chance"),
                ["HEALTH_REGEN"] = new("HEALTH REGEN", "+1.5 health per second"),
                ["FIRE_RESISTANCE"] = new("FIRE RESISTANCE", "+6% fire resistance"),
                ["COLD_RESISTANCE"] = new("COLD RESISTANCE", "+6% cold resistance"),
                ["LIGHTNING_RESISTANCE"] = new("LIGHTNING RESISTANCE", "+6% lightning resistance"),
                ["POISON_RESISTANCE"] = new("POISON RESISTANCE", "+6% poison resistance"),
                ["HOLY_RESISTANCE"] = new("HOLY RESISTANCE", "+6% holy resistance"),
                ["SHADOW_RESISTANCE"] = new("SHADOW RESISTANCE", "+6% shadow resistance"),
                ["ARCANE_RESISTANCE"] = new("ARCANE RESISTANCE", "+6% arcane resistance"),
            };

        private PanelSettings? _panelSettings;
        private VisualElement? _root;
        private VisualElement? _window;
        private VisualElement? _offerGrid;
        private Label? _roundLabel;
        private Label? _goldBalance;
        private Button? _readyButton;
        private DbConnection? _connection;
        private readonly Dictionary<string, float> _pendingPurchases = new(StringComparer.Ordinal);
        private string _lastSignature = string.Empty;
        private bool _open;

        private readonly struct ModifierCopy
        {
            public ModifierCopy(string name, string detail)
            {
                Name = name;
                Detail = detail;
            }

            public string Name { get; }
            public string Detail { get; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<SurvivalShopScreen>() != null)
                return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            GameObject host = new("SurvivalShopScreen");
            DontDestroyOnLoad(host);
            host.AddComponent<SurvivalShopScreen>();
        }

        private void Awake()
        {
            RuntimeUiEventSystem.Ensure();
            UIDocument document = ArenaPanel.CreateDocument(gameObject, "UI/Toolkit/SurvivalShop", 62f);
            _panelSettings = document.panelSettings;
            _root = document.rootVisualElement.Q<VisualElement>("SurvivalShop");
            _window = _root?.Q<VisualElement>("Window");
            _offerGrid = _root?.Q<VisualElement>("OfferGrid");
            _roundLabel = _root?.Q<Label>("RoundLabel");
            _goldBalance = _root?.Q<Label>("GoldBalance");
            _readyButton = _root?.Q<Button>("ReadyButton");
            if (_root == null || _window == null || _offerGrid == null || _roundLabel == null
                || _goldBalance == null || _readyButton == null)
            {
                Debug.LogError("SurvivalShopScreen: SurvivalShop.uxml binding contract is incomplete.");
                return;
            }
            _readyButton.clicked += ReadyForRound;
        }

        private void OnDestroy()
        {
            SubscribeToConnection(null);
            if (_panelSettings != null)
                Destroy(_panelSettings);
        }

        private void Update()
        {
            DbConnection? conn = NetworkManager.Instance?.Conn;
            SubscribeToConnection(conn);
            if (_root == null || conn == null || !conn.Identity.HasValue
                || !ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
            {
                SetOpen(false);
                return;
            }

            SurvivalRun? run = conn.Db.SurvivalRun.Owner.Filter(conn.Identity.Value).FirstOrDefault();
            bool shouldOpen = run != null
                && MatchStateCache.Instance.IsSurvivalMode
                && string.Equals(run.Phase, IntermissionPhase, StringComparison.Ordinal);
            SetOpen(shouldOpen);
            if (!shouldOpen || run == null)
                return;

            Refresh(run, conn);
        }

        private void SubscribeToConnection(DbConnection? conn)
        {
            if (ReferenceEquals(conn, _connection))
                return;
            if (_connection != null)
                _connection.Reducers.OnPurchaseSurvivalOffer -= OnPurchaseSurvivalOffer;
            _connection = conn;
            _pendingPurchases.Clear();
            _lastSignature = string.Empty;
            if (_connection != null)
                _connection.Reducers.OnPurchaseSurvivalOffer += OnPurchaseSurvivalOffer;
        }

        private void OnPurchaseSurvivalOffer(ReducerEventContext ctx, string offerId)
        {
            _pendingPurchases.Remove(offerId);
            _lastSignature = string.Empty;
        }

        private void SetOpen(bool open)
        {
            if (_root == null || _window == null || _open == open)
                return;
            _open = open;
            if (open)
            {
                if (_panelSettings != null)
                    _panelSettings.sortingOrder = RuntimeUiLayer.NextSortingOrder();
                _root.AddToClassList(OpenClass);
                _window.AddToClassList(OpenClass);
            }
            else
            {
                _window.RemoveFromClassList(OpenClass);
                _root.RemoveFromClassList(OpenClass);
                _lastSignature = string.Empty;
            }
        }

        private void Refresh(SurvivalRun run, DbConnection conn)
        {
            uint shopRound = Math.Max(1u, run.Round);
            List<SurvivalShopOffer> offers = conn.Db.SurvivalShopOffer.ArenaId
                .Filter(run.ArenaId)
                .Where(offer => offer.Round == shopRound)
                .OrderBy(offer => offer.OfferId, StringComparer.Ordinal)
                .ToList();
            string signature = $"{run.ArenaId}|{shopRound}|{run.Gold}|" + string.Join(
                ";",
                offers.Select(offer => $"{offer.OfferId}:{offer.Purchased}"));
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
                return;
            _lastSignature = signature;

            _roundLabel!.text = $"PREPARE FOR ROUND {shopRound}";
            _goldBalance!.text = $"{run.Gold:N0} GOLD";
            _readyButton!.text = $"START ROUND {shopRound}";
            _offerGrid!.Clear();
            foreach (SurvivalShopOffer offer in offers)
                _offerGrid.Add(BuildOfferCard(offer, run.Gold, conn));
        }

        private Button BuildOfferCard(SurvivalShopOffer offer, ulong gold, DbConnection conn)
        {
            (string kind, string name, string detail) = ResolveOfferCopy(offer, conn);
            Button card = new() { name = $"Offer_{offer.OfferId}", text = string.Empty };
            card.AddToClassList("offer-card");
            card.Add(MakeLabel(kind, "offer-kind"));
            card.Add(MakeLabel(name, "offer-name"));
            card.Add(MakeLabel(offer.Purchased ? "Purchased" : detail, "offer-detail"));
            card.Add(MakeLabel(offer.Purchased ? "OWNED" : $"{offer.Price:N0} GOLD", "offer-price"));
            if (offer.Purchased)
                card.AddToClassList(PurchasedClass);

            bool pending = _pendingPurchases.TryGetValue(offer.OfferId, out float requestedAt)
                && Time.unscaledTime - requestedAt < 3f;
            if (!pending)
                _pendingPurchases.Remove(offer.OfferId);
            card.SetEnabled(!offer.Purchased && !pending && gold >= offer.Price);
            if (!offer.Purchased)
            {
                string offerId = offer.OfferId;
                card.clicked += () => Purchase(offerId);
            }
            return card;
        }

        private static Label MakeLabel(string text, string className)
        {
            Label label = new(text);
            label.AddToClassList(className);
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        private static (string Kind, string Name, string Detail) ResolveOfferCopy(
            SurvivalShopOffer offer,
            DbConnection conn)
        {
            if (string.Equals(offer.Kind, "MODIFIER", StringComparison.Ordinal))
            {
                ModifierCopy copy = ModifierCopies.TryGetValue(offer.ModifierId, out ModifierCopy known)
                    ? known
                    : new ModifierCopy(offer.ModifierId.Replace('_', ' '), "Permanent for this run");
                return ("MODIFIER", copy.Name, copy.Detail);
            }

            ItemInstance? item = conn.Db.ItemInstance.ItemInstanceId.Find(offer.ItemInstanceId);
            ItemDefinition? definition = item == null
                ? null
                : conn.Db.ItemDefinition.ItemDefId.Find(item.ItemDefId);
            int affixCount = conn.Db.ItemAffixInstance.ItemInstanceId.Filter(offer.ItemInstanceId).Count();
            string affixLabel = affixCount == 1 ? "1 AFFIX" : $"{affixCount} AFFIXES";
            string name = definition?.DisplayName ?? "EQUIPMENT";
            string detail = definition == null
                ? "Rolled equipment"
                : EquipmentDetail(definition);
            return ($"EQUIPMENT · {affixLabel}", name.ToUpperInvariant(), detail);
        }

        private static string EquipmentDetail(ItemDefinition definition)
        {
            if (!string.IsNullOrWhiteSpace(definition.WeaponKind))
                return definition.WeaponKind.Replace('_', ' ').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(definition.EquipSlot))
                return definition.EquipSlot.Replace('_', ' ').ToLowerInvariant();
            return definition.ItemKind.Replace('_', ' ').ToLowerInvariant();
        }

        private void Purchase(string offerId)
        {
            if (_connection == null || _pendingPurchases.ContainsKey(offerId))
                return;
            _pendingPurchases[offerId] = Time.unscaledTime;
            _lastSignature = string.Empty;
            _connection.Reducers.PurchaseSurvivalOffer(offerId);
        }

        private void ReadyForRound()
        {
            _connection?.Reducers.ReadyForNextSurvivalRound();
        }
    }
}
