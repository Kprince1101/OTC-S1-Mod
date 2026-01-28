using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI.Phone.Messages;
using MelonLoader;
using S1API.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheCounter.UI
{
    /// <summary>
    /// Custom UI for selecting a delivery location for desperation deals.
    /// Displays all delivery locations in the customer's region.
    /// </summary>
    public static class LocationPickerUI
    {
        private static GameObject _pickerRoot;
        private static Action<string> _onLocationSelected;
        private static Customer _currentCustomer;

        /// <summary>
        /// Shows the location picker for a customer's region.
        /// </summary>
        public static void Show(Customer customer, Action<string> onSelected)
        {
            if (customer == null || customer.NPC == null)
            {
                MelonLogger.Error("[LocationPickerUI] Customer is null");
                return;
            }

            _currentCustomer = customer;
            _onLocationSelected = onSelected;

            // Get delivery locations for the customer's region
            var locations = GetDeliveryLocationsForRegion(customer.NPC.Region);

            if (locations.Count == 0)
            {
                MelonLogger.Warning($"[LocationPickerUI] No delivery locations found for region {customer.NPC.Region}");
                return;
            }

            // Create the UI
            CreatePickerUI(locations);
        }

        /// <summary>
        /// Gets all delivery locations for a customer's region using reflection.
        /// </summary>
        private static List<LocationInfo> GetDeliveryLocationsForRegion(object region)
        {
            var locations = new List<LocationInfo>();

            try
            {
                // Get Map singleton via reflection
                var mapType = Type.GetType("Il2CppScheduleOne.Map.Map, Assembly-CSharp");
                if (mapType == null)
                {
                    MelonLogger.Warning("[LocationPickerUI] Could not find Map type");
                    return locations;
                }

                var singletonType = typeof(Singleton<>).MakeGenericType(mapType);
                var instanceProp = singletonType.GetProperty("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var mapInstance = instanceProp?.GetValue(null);

                if (mapInstance == null)
                {
                    MelonLogger.Warning("[LocationPickerUI] Map instance is null");
                    return locations;
                }

                // Get region data
                var getRegionDataMethod = mapInstance.GetType().GetMethod("GetRegionData");
                if (getRegionDataMethod == null)
                {
                    MelonLogger.Warning("[LocationPickerUI] Could not find GetRegionData method");
                    return locations;
                }

                var regionData = getRegionDataMethod.Invoke(mapInstance, new object[] { region });
                if (regionData == null)
                {
                    MelonLogger.Warning($"[LocationPickerUI] No region data for {region}");
                    return locations;
                }

                // Get delivery locations from the region
                var deliveryLocationsProp = regionData.GetType().GetProperty("RegionDeliveryLocations");
                if (deliveryLocationsProp == null)
                {
                    MelonLogger.Warning($"[LocationPickerUI] Could not find RegionDeliveryLocations property on {regionData.GetType().Name}");
                    return locations;
                }

                dynamic deliveryLocations = deliveryLocationsProp.GetValue(regionData);
                if (deliveryLocations == null)
                {
                    MelonLogger.Warning("[LocationPickerUI] RegionDeliveryLocations is null");
                    return locations;
                }

                int count = deliveryLocations.Length;
                for (int i = 0; i < count; i++)
                {
                    dynamic loc = deliveryLocations[i];
                    if (loc != null)
                    {
                        locations.Add(new LocationInfo
                        {
                            GUID = loc.GUID.ToString(),
                            Name = loc.LocationName,
                            Description = loc.LocationDescription ?? ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[LocationPickerUI] Error getting locations: {ex.Message}");
            }

            return locations;
        }

        /// <summary>
        /// Creates the picker UI using S1API UIFactory helpers.
        /// </summary>
        private static void CreatePickerUI(List<LocationInfo> locations)
        {
            // Destroy existing picker if any
            if (_pickerRoot != null)
            {
                UnityEngine.Object.Destroy(_pickerRoot);
            }

            // Create our own root canvas to render on top of phone UI
            _pickerRoot = new GameObject("LocationPickerRoot");
            var rootCanvas = _pickerRoot.AddComponent<Canvas>();
            rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            rootCanvas.sortingOrder = 90; // Above phone UI, below loading screen (100)

            // Required components for UI interaction
            _pickerRoot.AddComponent<CanvasScaler>();
            _pickerRoot.AddComponent<GraphicRaycaster>();

            // Create dark overlay (full screen)
            var overlayObj = UIFactory.Panel("Overlay", _pickerRoot.transform, new Color(0, 0, 0, 0.7f), fullAnchor: true);
            overlayObj.GetComponent<Image>().raycastTarget = true;

            // Create center panel (compact, centered)
            var panelObj = UIFactory.Panel("LocationPanel", _pickerRoot.transform, new Color(0.18f, 0.18f, 0.18f));
            var panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(280, 400);

            // Title using UIFactory
            var titleText = UIFactory.Text("Title", "<b>Select Location</b>", panelObj.transform, 16, TextAnchor.MiddleCenter);
            PositionAtTop(titleText.gameObject.GetComponent<RectTransform>(), -8, 25);

            // Subtitle using UIFactory
            var subtitleText = UIFactory.Text("Subtitle", $"Where should {_currentCustomer.NPC.FirstName} meet you?", panelObj.transform, 12, TextAnchor.MiddleCenter);
            subtitleText.color = new Color(0.7f, 0.7f, 0.7f);
            PositionAtTop(subtitleText.gameObject.GetComponent<RectTransform>(), -35, 18);

            // Use UIFactory's ScrollableVerticalList instead of manual setup
            var listContent = UIFactory.ScrollableVerticalList("LocationList", panelObj.transform, out ScrollRect scrollRect);
            
            // Position the scroll view between title and cancel button
            var scrollRectTransform = scrollRect.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = new Vector2(0, 0);
            scrollRectTransform.anchorMax = new Vector2(1, 1);
            scrollRectTransform.offsetMin = new Vector2(8, 45); // Leave space for cancel button
            scrollRectTransform.offsetMax = new Vector2(-8, -58); // Leave space for title/subtitle
            
            // Configure the existing VerticalLayoutGroup created by ScrollableVerticalList
            var contentLayout = listContent.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.spacing = 4;
                contentLayout.padding = new RectOffset(4, 4, 4, 4);
                contentLayout.childControlHeight = true;
                contentLayout.childControlWidth = true;
                contentLayout.childForceExpandHeight = false;
                contentLayout.childForceExpandWidth = true;
            }

            // Create location buttons
            foreach (var loc in locations)
            {
                CreateLocationButton(listContent, loc);
            }

            // Force layout rebuild
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(listContent);

            // Cancel button at bottom using UIFactory
            var (cancelMask, cancelBtn, cancelLabel) = UIFactory.RoundedButtonWithLabel(
                "CancelBtn", 
                "Cancel", 
                panelObj.transform, 
                new Color(0.5f, 0.2f, 0.2f), 
                264, // width (280 - 16 margin)
                30,  // height
                12,  // fontSize
                Color.white
            );
            
            // Position cancel button at bottom
            var cancelRect = cancelMask.GetComponent<RectTransform>();
            cancelRect.anchorMin = new Vector2(0.5f, 0);
            cancelRect.anchorMax = new Vector2(0.5f, 0);
            cancelRect.pivot = new Vector2(0.5f, 0);
            cancelRect.anchoredPosition = new Vector2(0, 8);

            cancelBtn.onClick.AddListener(new Action(() =>
            {
                Hide();
            }));

            _pickerRoot.SetActive(true);
        }

        /// <summary>
        /// Helper to position UI elements at top of parent with specific offset and height.
        /// </summary>
        private static void PositionAtTop(RectTransform rect, float yOffset, float height)
        {
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, yOffset);
            rect.sizeDelta = new Vector2(0, height);
        }

        /// <summary>
        /// Creates a button for a delivery location using UIFactory.
        /// </summary>
        private static void CreateLocationButton(Transform parent, LocationInfo location)
        {
            try
            {
                // Use UIFactory.ButtonWithLabel for cleaner button creation
                var (btnObj, btn, btnText) = UIFactory.ButtonWithLabel(
                    $"Loc_{location.Name}",
                    location.Name,
                    parent,
                    new Color(0.25f, 0.42f, 0.25f),
                    260, // width
                    32   // height
                );

                if (btnObj == null)
                {
                    MelonLogger.Error($"[LocationPickerUI] Failed to create button for {location.Name}");
                    return;
                }

                // Add LayoutElement for proper sizing in vertical list
                var btnLayout = btnObj.AddComponent<LayoutElement>();
                btnLayout.preferredHeight = 32f;
                btnLayout.minHeight = 32f;

                // Configure button colors for hover/press states
                var colors = btn.colors;
                colors.normalColor = new Color(0.25f, 0.42f, 0.25f);
                colors.highlightedColor = new Color(0.35f, 0.55f, 0.35f);
                colors.pressedColor = new Color(0.2f, 0.3f, 0.2f);
                colors.selectedColor = new Color(0.25f, 0.42f, 0.25f);
                btn.colors = colors;

                // Configure text: smaller font size and add horizontal padding to prevent clipping
                btnText.fontSize = 11;
                btnText.color = Color.white;
                var textRect = btnText.GetComponent<RectTransform>();
                textRect.offsetMin = new Vector2(5, 0);  // left padding
                textRect.offsetMax = new Vector2(-5, 0); // right padding

                // Capture location for callback
                string locGuid = location.GUID;
                btn.onClick.AddListener(new Action(() =>
                {
                    var callback = _onLocationSelected;
                    Hide();
                    callback?.Invoke(locGuid);
                }));
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[LocationPickerUI] Error creating button for {location.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Hides the location picker.
        /// </summary>
        public static void Hide()
        {
            if (_pickerRoot != null)
            {
                UnityEngine.Object.Destroy(_pickerRoot);
                _pickerRoot = null;
            }
            _onLocationSelected = null;
            _currentCustomer = null;
        }

        /// <summary>
        /// Info about a delivery location.
        /// </summary>
        private class LocationInfo
        {
            public string GUID { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
        }
    }
}
