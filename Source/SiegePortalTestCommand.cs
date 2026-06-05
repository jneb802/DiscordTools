using System;
using System.Globalization;
using UnityEngine;

namespace DiscordTools
{
    internal static class SiegePortalTestCommand
    {
        private static bool _registered;

        internal static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            _ = new Terminal.ConsoleCommand(
                "dt_mark_siege_portal",
                "Mark the nearest portal as a valheimCreative siege portal. Usage: dt_mark_siege_portal <siegeId> [radius]",
                args =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString("Usage: dt_mark_siege_portal <siegeId> [radius]");
                        return;
                    }

                    string siegeId = args[1].Trim();
                    if (string.IsNullOrWhiteSpace(siegeId))
                    {
                        args.Context.AddString("siegeId is required.");
                        return;
                    }

                    float radius = 12f;
                    if (args.Length >= 3)
                    {
                        float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                    }

                    Player player = Player.m_localPlayer;
                    if (player == null)
                    {
                        args.Context.AddString("No local player found.");
                        return;
                    }

                    TeleportWorld? portal = FindNearestPortal(player.transform.position, Mathf.Clamp(radius, 1f, 50f), out float distance);
                    if (portal == null)
                    {
                        args.Context.AddString($"No portal found within {radius:0.#}m.");
                        return;
                    }

                    ZNetView view = portal.GetComponent<ZNetView>();
                    ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                    if (zdo == null)
                    {
                        args.Context.AddString("Nearest portal has no valid ZDO.");
                        return;
                    }

                    zdo.Set(SiegePortalBridge.SiegeIdZdoKey, siegeId);
                    zdo.Set(ZDOVars.s_tag, SiegePortalBridge.SiegeTagPrefix + siegeId);
                    args.Context.AddString($"Marked nearest portal {zdo.m_uid} as siege {siegeId} at {distance:0.#}m.");
                });

            _ = new Terminal.ConsoleCommand(
                "dt_enter_nearest_siege_portal",
                "Enter the nearest marked valheimCreative siege portal. Usage: dt_enter_nearest_siege_portal [radius]",
                args =>
                {
                    float radius = 12f;
                    if (args.Length >= 2)
                    {
                        float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                    }

                    Player player = Player.m_localPlayer;
                    if (player == null)
                    {
                        args.Context.AddString("No local player found.");
                        return;
                    }

                    TeleportWorld? portal = FindNearestPortal(player.transform.position, Mathf.Clamp(radius, 1f, 50f), out float distance);
                    if (portal == null)
                    {
                        args.Context.AddString($"No portal found within {radius:0.#}m.");
                        return;
                    }

                    bool handled = SiegePortalBridge.TryHandle(portal, player);
                    args.Context.AddString($"Nearest siege portal handled={handled} distance={distance:0.#}m.");
                });
        }

        private static TeleportWorld? FindNearestPortal(Vector3 center, float radius, out float distance)
        {
            distance = float.MaxValue;
            TeleportWorld? best = null;
            foreach (Collider collider in Physics.OverlapSphere(center, radius))
            {
                TeleportWorld portal = collider.GetComponentInParent<TeleportWorld>();
                if (portal == null)
                {
                    continue;
                }

                float current = Vector3.Distance(center, portal.transform.position);
                if (current < distance)
                {
                    distance = current;
                    best = portal;
                }
            }

            return best;
        }
    }
}
