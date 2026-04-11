using OverTheCounter.Utilities;
using S1API.Internal.Abstraction;
using S1API.Saveables;
using System;
using System.Collections.Generic;

#if IL2CPP
using ProductDefinition = Il2CppScheduleOne.Product.ProductDefinition;
#else
using ProductDefinition = ScheduleOne.Product.ProductDefinition;
#endif

namespace OverTheCounter.SaveData
{
    /// <summary>
    /// Per-product price override entry. Stored in save data.
    /// </summary>
    [Serializable]
    public class ProductPriceEntry
    {
        public string ProductId;
        /// <summary>Manual price override. -1 means not set (use auto-pricing).</summary>
        public float ManualPrice = -1f;
        /// <summary>When true, this product will not be sold to customers.</summary>
        public bool SellingDisabled;
    }

    /// <summary>
    /// Global pricing configuration for OTC dispensaries.
    /// Provides auto-pricing (MarketValue * multiplier) with per-product manual overrides.
    /// Separate from PropertySaveData for future warehouse/illegal product separation.
    /// </summary>
    public class PricingSaveData : Saveable
    {
        [SaveableField("otc_auto_pricing_enabled")]
        private bool _autoPricingEnabled = true;

        [SaveableField("otc_pricing_multiplier")]
        private float _pricingMultiplier = 1.25f;

        [SaveableField("otc_product_overrides")]
        private List<ProductPriceEntry> _overrides = new();

        /// <summary>Singleton instance, set during construction or load.</summary>
        public static PricingSaveData Instance { get; private set; }

        internal static void ResetInstance() => Instance = null;

        public PricingSaveData()
        {
            Instance = this;
        }

        /// <summary>
        /// Ensures a runtime instance exists. On host, S1API constructs one from
        /// the save file. On client, Saveables are host-only — call this before
        /// any code path that needs a non-null <see cref="Instance"/> so the
        /// client can hold synced pricing state and drive its own UI.
        /// </summary>
        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            try { _ = new PricingSaveData(); }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.General,
                    $"PricingSaveData.EnsureInstance failed: {ex.Message}");
            }
        }

        protected override void OnLoaded()
        {
            Instance = this;
            _overrides ??= new List<ProductPriceEntry>();
            OTCLog.Msg(OTCLog.Systems.General,
                $"PricingSaveData.OnLoaded — auto={_autoPricingEnabled}, mult={_pricingMultiplier:F2}, overrides={_overrides.Count}");
        }

        // =================================================================
        //  Public API — global settings
        // =================================================================

        /// <summary>Whether auto-pricing is enabled (MarketValue * multiplier).</summary>
        public bool AutoPricingEnabled
        {
            get => _autoPricingEnabled;
            set => _autoPricingEnabled = value;
        }

        /// <summary>Auto-pricing multiplier applied to MarketValue. Default 1.25.</summary>
        public float PricingMultiplier
        {
            get => _pricingMultiplier;
            set => _pricingMultiplier = Math.Max(0.01f, value);
        }

        // =================================================================
        //  Public API — price resolution
        // =================================================================

        /// <summary>
        /// Resolves the selling price for a product.
        /// Priority: manual override > auto-price > MarketValue.
        /// </summary>
        public float GetPrice(ProductDefinition prodDef)
        {
            if (prodDef == null) return 0f;

            var entry = FindOverride(prodDef.ID);
            if (entry != null && entry.ManualPrice >= 0f)
                return entry.ManualPrice;

            float baseValue = prodDef.MarketValue > 0f ? prodDef.MarketValue : 1f;
            return _autoPricingEnabled ? baseValue * _pricingMultiplier : baseValue;
        }

        /// <summary>Whether selling is disabled for a specific product.</summary>
        public bool IsSellingDisabled(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return false;
            var entry = FindOverride(productId);
            return entry != null && entry.SellingDisabled;
        }

        // =================================================================
        //  Public API — per-product overrides
        // =================================================================

        /// <summary>Sets a manual price for a product, overriding auto-pricing.</summary>
        public void SetManualPrice(string productId, float price)
        {
            var entry = GetOrCreateOverride(productId);
            entry.ManualPrice = Math.Max(0f, price);
        }

        /// <summary>Clears the manual price, reverting to auto-pricing.</summary>
        public void ClearManualPrice(string productId)
        {
            var entry = FindOverride(productId);
            if (entry == null) return;
            entry.ManualPrice = -1f;
            CleanupIfDefault(entry);
        }

        /// <summary>Enables or disables selling for a specific product.</summary>
        public void SetSellingDisabled(string productId, bool disabled)
        {
            if (disabled)
            {
                var entry = GetOrCreateOverride(productId);
                entry.SellingDisabled = true;
            }
            else
            {
                var entry = FindOverride(productId);
                if (entry == null) return;
                entry.SellingDisabled = false;
                CleanupIfDefault(entry);
            }
        }

        /// <summary>Returns the override entry for a product, or null if none exists.</summary>
        public ProductPriceEntry GetOverride(string productId) =>
            FindOverride(productId);

        // =================================================================
        //  Serialization for network sync
        // =================================================================

        /// <summary>
        /// Serializes pricing state for SyncVar transmission.
        /// Format: "enabled|multiplier|prodId:price:disabled,prodId:price:disabled,..."
        /// </summary>
        public string Serialize()
        {
            string overrides = "";
            if (_overrides.Count > 0)
            {
                var parts = new List<string>();
                foreach (var o in _overrides)
                {
                    parts.Add($"{o.ProductId}:{o.ManualPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{(o.SellingDisabled ? "1" : "0")}");
                }
                overrides = string.Join(",", parts);
            }
            return $"{(_autoPricingEnabled ? "1" : "0")}|{_pricingMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{overrides}";
        }

        /// <summary>
        /// Applies pricing state received from host SyncVar.
        /// </summary>
        public void Deserialize(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            var segments = data.Split('|');
            if (segments.Length < 2) return;

            _autoPricingEnabled = segments[0] == "1";
            if (float.TryParse(segments[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float mult))
                _pricingMultiplier = mult;

            _overrides.Clear();
            if (segments.Length >= 3 && !string.IsNullOrEmpty(segments[2]))
            {
                var entries = segments[2].Split(',');
                foreach (var entry in entries)
                {
                    var fields = entry.Split(':');
                    if (fields.Length < 3) continue;

                    float.TryParse(fields[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float price);

                    _overrides.Add(new ProductPriceEntry
                    {
                        ProductId = fields[0],
                        ManualPrice = price,
                        SellingDisabled = fields[2] == "1"
                    });
                }
            }
        }

        // =================================================================
        //  Internals
        // =================================================================

        private ProductPriceEntry FindOverride(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return null;
            for (int i = 0; i < _overrides.Count; i++)
            {
                if (_overrides[i].ProductId == productId)
                    return _overrides[i];
            }
            return null;
        }

        private ProductPriceEntry GetOrCreateOverride(string productId)
        {
            var entry = FindOverride(productId);
            if (entry != null) return entry;
            entry = new ProductPriceEntry { ProductId = productId };
            _overrides.Add(entry);
            return entry;
        }

        /// <summary>Removes override entry if it has no meaningful values (saves space).</summary>
        private void CleanupIfDefault(ProductPriceEntry entry)
        {
            if (entry.ManualPrice < 0f && !entry.SellingDisabled)
                _overrides.Remove(entry);
        }
    }
}
