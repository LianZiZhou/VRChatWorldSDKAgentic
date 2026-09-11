using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Grabbing, dropping, sitting and standing, driven the way ClientSim drives them.
    ///
    /// These are the two interactions a world is most often wrong about and that a scripted scenario could
    /// not reach before: a pickup whose OnPickup never fires because the field was set on the proxy, and a
    /// station whose OnStationEntered was never tested because entering one needs a player.
    ///
    /// Stations need no reflection into privates — ClientSimStationHelper.UseStation and ExitStation are
    /// public statics, and they are the same entry points VRCStation.UseStation itself goes through in
    /// ClientSim, so a scripted sit is indistinguishable from a player sitting down.
    ///
    /// Pickups are one step less clean. ClientSimPlayerHand.Pickup is private and the grab path is driven by
    /// device input events that no public API raises, so the choice is between reflecting into that one
    /// method or faking it by invoking the Udon events directly. Faking it would prove nothing — the point of
    /// the step is that ClientSim's own pickup machinery runs, the hand takes the object, VRC_Pickup's state
    /// updates and the events fire from there. So: reflection, with the signature checked and a readable
    /// error if a future SDK moves it.
    ///
    /// What a scripted grab does NOT check is reachability: the hand takes the object wherever it is. That is
    /// the same compromise the interact step makes, and for the same reason — proving a player could walk up
    /// to it and aim at it is scene_spatial_audit's job, not a behaviour scenario's.
    /// </summary>
    public static class ClientSimPlayerActions
    {
        private const string PickupBase = "VRC.SDKBase.VRC_Pickup";
        private const string PickupHelper = "VRC.SDK3.ClientSim.ClientSimPickupHelper";
        private const string StationComponent = "VRC.SDK3.Components.VRCStation";
        private const string StationHelper = "VRC.SDK3.ClientSim.ClientSimStationHelper";
        private const string PlayerHand = "VRC.SDK3.ClientSim.ClientSimPlayerHand";

        // ------------------------------------------------------------------ pickups

        public static Dictionary<string, object> Grab(GameObject target, string hand)
        {
            var pickup = RequirePickup(target);
            var helper = EnsurePickupHelper(pickup);

            var hands = FindHands();
            if (hands.Count == 0)
                throw new InvalidOperationException(
                    "No ClientSimPlayerHand in the scene. ClientSim is not running with a local player, so " +
                    "nothing can hold a pickup; check that spawnPlayer is enabled for this scenario.");

            var chosen = ChooseHand(hands, hand);
            string chosenName = HandName(chosen);

            if (IsHolding(chosen))
                throw new InvalidOperationException(
                    "The " + chosenName + " hand is already holding something. Drop it first, or grab with the other hand.");

            var pickupMethod = chosen.GetType().GetMethod(
                "Pickup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (pickupMethod == null || pickupMethod.GetParameters().Length != 1)
                throw new MissingMethodException(
                    "ClientSim API moved: ClientSimPlayerHand.Pickup(IClientSimPickupable) was not found. " +
                    "The grab step needs it because no public API raises a grab.");

            pickupMethod.Invoke(chosen, new object[] { helper });

            object holder = HeldBy(target);
            return new Dictionary<string, object>
            {
                { "hand", chosenName },
                { "held", holder != null },
                { "heldBy", DisplayName(holder) }
            };
        }

        public static Dictionary<string, object> Drop(GameObject target)
        {
            var pickup = RequirePickup(target);
            bool wasHeld = HeldBy(target) != null;

            var helperType = Resolve(PickupHelper);
            var forceDrop = helperType.GetMethod("ForceDrop", BindingFlags.Static | BindingFlags.Public);
            if (forceDrop == null)
                throw new MissingMethodException("ClientSim API moved: ClientSimPickupHelper.ForceDrop(VRC_Pickup)");
            forceDrop.Invoke(null, new object[] { pickup });

            return new Dictionary<string, object>
            {
                { "wasHeld", wasHeld },
                { "held", HeldBy(target) != null }
            };
        }

        /// <summary>The player holding this pickup, or null. Read through ClientSim's own accessor.</summary>
        public static object HeldBy(GameObject target)
        {
            var pickup = target.GetComponent(Resolve(PickupBase));
            if (pickup == null) return null;

            var helperType = Resolve(PickupHelper);
            var current = helperType.GetMethod("GetCurrentPlayer", BindingFlags.Static | BindingFlags.Public);
            if (current == null) return null;
            return current.Invoke(null, new object[] { pickup });
        }

        private static Component RequirePickup(GameObject target)
        {
            var pickup = target.GetComponent(Resolve(PickupBase));
            if (pickup == null)
                throw new ArgumentException(
                    UdonOps.HierarchyPath(target.transform) + " has no VRCPickup component, so it cannot be picked up. " +
                    "Add one (it also needs a Rigidbody and a collider) before grabbing it.");
            return pickup;
        }

        /// <summary>
        /// ClientSim adds a helper to every pickup when it starts. One added to the scene afterwards has
        /// none, so create it rather than failing — InitializePickup is public and is what ClientSim calls.
        /// </summary>
        private static Component EnsurePickupHelper(Component pickup)
        {
            var helperType = Resolve(PickupHelper);
            var helper = pickup.gameObject.GetComponent(helperType);
            if (helper != null) return helper;

            var initialize = helperType.GetMethod("InitializePickup", BindingFlags.Static | BindingFlags.Public);
            if (initialize == null)
                throw new MissingMethodException("ClientSim API moved: ClientSimPickupHelper.InitializePickup(VRC_Pickup)");
            initialize.Invoke(null, new object[] { pickup });

            helper = pickup.gameObject.GetComponent(helperType);
            if (helper == null)
                throw new InvalidOperationException("ClientSimPickupHelper could not be created on " + pickup.gameObject.name);
            return helper;
        }

        // ------------------------------------------------------------------ hands

        /// <summary>
        /// Every player hand in the loaded scenes.
        ///
        /// FindObjectsOfType is not usable here: the hand disables itself when it is not holding anything, to
        /// avoid an Update per frame. FindObjectsOfTypeAll sees it anyway, and the scene check drops the
        /// prefab copies it also returns.
        /// </summary>
        private static List<Component> FindHands()
        {
            var handType = Resolve(PlayerHand);
            var found = new List<Component>();
            foreach (var obj in Resources.FindObjectsOfTypeAll(handType))
            {
                var component = obj as Component;
                if (component == null) continue;
                if (!component.gameObject.scene.IsValid()) continue;   // a prefab asset, not a live hand
                if (component.gameObject.hideFlags != HideFlags.None) continue;
                found.Add(component);
            }
            return found;
        }

        private static Component ChooseHand(List<Component> hands, string requested)
        {
            string want = string.IsNullOrEmpty(requested) ? "right" : requested.Trim().ToLowerInvariant();
            if (want != "left" && want != "right" && want != "either")
                throw new ArgumentException("hand must be \"left\", \"right\" or \"either\"; got \"" + requested + "\"");

            if (want != "either")
            {
                foreach (var hand in hands)
                    if (string.Equals(HandName(hand), want, StringComparison.OrdinalIgnoreCase)) return hand;

                // Named hand not found: say what is there rather than silently using the other one.
                var names = new List<string>();
                foreach (var hand in hands) names.Add(HandName(hand));
                throw new InvalidOperationException(
                    "No " + want + " hand in this ClientSim player. Hands present: " + string.Join(", ", names.ToArray()));
            }

            foreach (var hand in hands)
                if (!IsHolding(hand)) return hand;
            return hands[0];
        }

        /// <summary>"left" or "right", read from the hand's serialized handType field.</summary>
        private static string HandName(Component hand)
        {
            var field = hand.GetType().GetField("handType", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return "unknown";
            object value = field.GetValue(hand);
            return value == null ? "unknown" : value.ToString().ToLowerInvariant();
        }

        private static bool IsHolding(Component hand)
        {
            var method = hand.GetType().GetMethod("IsHolding", BindingFlags.Instance | BindingFlags.Public);
            if (method == null) return false;
            return (bool)method.Invoke(hand, null);
        }

        // ------------------------------------------------------------------ stations

        public static Dictionary<string, object> Sit(GameObject target)
        {
            var station = RequireStation(target);
            EnsureStationHelper(station);

            object player = LocalPlayer();
            InvokeStationStatic("UseStation", station, player);

            object occupant = StationOccupant(target);
            return new Dictionary<string, object>
            {
                { "seated", occupant != null },
                { "player", DisplayName(occupant) }
            };
        }

        public static Dictionary<string, object> Stand(GameObject target)
        {
            var station = RequireStation(target);
            EnsureStationHelper(station);
            bool wasSeated = StationOccupant(target) != null;

            object player = LocalPlayer();
            InvokeStationStatic("ExitStation", station, player);

            return new Dictionary<string, object>
            {
                { "wasSeated", wasSeated },
                { "seated", StationOccupant(target) != null }
            };
        }

        /// <summary>The player sitting in this station, or null.</summary>
        public static object StationOccupant(GameObject target)
        {
            var helper = target.GetComponent(Resolve(StationHelper));
            if (helper == null) return null;
            var method = helper.GetType().GetMethod("GetCurrentSittingPlayer", BindingFlags.Instance | BindingFlags.Public);
            return method == null ? null : method.Invoke(helper, null);
        }

        private static Component RequireStation(GameObject target)
        {
            var station = target.GetComponent(Resolve(StationComponent));
            if (station == null)
                throw new ArgumentException(
                    UdonOps.HierarchyPath(target.transform) + " has no VRCStation component, so nobody can sit in it.");
            return station;
        }

        private static void EnsureStationHelper(Component station)
        {
            var helperType = Resolve(StationHelper);
            if (station.gameObject.GetComponent(helperType) != null) return;

            var initialize = helperType.GetMethod("InitializeStations", BindingFlags.Static | BindingFlags.Public);
            if (initialize == null)
                throw new MissingMethodException("ClientSim API moved: ClientSimStationHelper.InitializeStations(VRCStation)");
            initialize.Invoke(null, new object[] { station });
        }

        private static void InvokeStationStatic(string name, Component station, object player)
        {
            var helperType = Resolve(StationHelper);
            var method = helperType.GetMethod(name, BindingFlags.Static | BindingFlags.Public);
            if (method == null)
                throw new MissingMethodException("ClientSim API moved: ClientSimStationHelper." + name + "(VRCStation, VRCPlayerApi)");
            method.Invoke(null, new object[] { station, player });
        }

        // ------------------------------------------------------------------ shared

        private static Type Resolve(string typeName)
        {
            var type = Reflect.ResolveType(typeName);
            if (type == null)
                throw new TypeLoadException(
                    typeName + " was not found. This step needs the VRChat SDK and ClientSim, and ClientSim's " +
                    "types only exist once its assembly is loaded.");
            return type;
        }

        private static object LocalPlayer()
        {
            var networking = Resolve("VRC.SDKBase.Networking");
            var property = networking.GetProperty("LocalPlayer", BindingFlags.Static | BindingFlags.Public);
            object player = property == null ? null : property.GetValue(null, null);
            if (player == null)
                throw new InvalidOperationException(
                    "There is no local player. ClientSim is not running, or it was configured without spawning one.");
            return player;
        }

        private static string DisplayName(object player)
        {
            if (player == null) return null;
            try
            {
                var field = player.GetType().GetField("displayName", BindingFlags.Instance | BindingFlags.Public);
                if (field != null) return Convert.ToString(field.GetValue(player));
                var property = player.GetType().GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public);
                if (property != null) return Convert.ToString(property.GetValue(player, null));
            }
            catch (Exception) { }
            return "player";
        }
    }
}
