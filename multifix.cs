using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Cvars;
using System.Collections.Generic;
using System;
using System.Runtime.InteropServices;

namespace BoosterPlugin
{
    public class ReplicatedConVarProcessor
    {
        private readonly Dictionary<int, Dictionary<string, (float Value, float EndTime)>> _playerConVars = new Dictionary<int, Dictionary<string, (float, float)>>();
        private readonly ConVar _gravityConVar = ConVar.Find("sv_gravity")!;
        private readonly MemoryFunctionVoid<CCSPlayer_MovementServices, IntPtr> _processMovement;
        private readonly BasePlugin _plugin;

        private readonly Dictionary<string, bool> _wasConVarChanged = new Dictionary<string, bool>
        {
            { "sv_gravity", false }
        };

        private float _defaultGravity = 800f;

        public ReplicatedConVarProcessor(BasePlugin plugin)
        {
            _plugin = plugin;
            string signature = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "55 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 83 EC 38 48 8B 7F 30"
                : "40 56 57 48 81 EC ? ? ? ? 4C 8B 49";

            _processMovement = new MemoryFunctionVoid<CCSPlayer_MovementServices, IntPtr>(signature);
            LogWithTimestamp($"[cs2-multifix] Loading with compatible ProcessMovement hook for {(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Windows")}...");

            _processMovement.Hook(ProcessMovementPre, HookMode.Pre);
            _processMovement.Hook(ProcessMovementPost, HookMode.Post);
        }

        public void Unload()
        {
            _processMovement.Unhook(ProcessMovementPre, HookMode.Pre);
            _processMovement.Unhook(ProcessMovementPost, HookMode.Post);
        }

        public void UpdateDefaultValues()
        {
            var gravityCvar = ConVar.Find("sv_gravity");
            if (gravityCvar != null)
            {
                _defaultGravity = gravityCvar.GetPrimitiveValue<float>();
                // Add a very explicit log to confirm default gravity
                LogWithTimestamp($"[cs2-multifix] DEFAULT GRAVITY VALUE: {_defaultGravity}");
            }
        }

        public void SetConVar(int slot, string conVarName, float value, float duration)
        {
            if (conVarName != "sv_gravity") return; // Only allow sv_gravity

            // Ensure we have a positive duration
            if (duration <= 0)
            {
                duration = 3.0f; // Default to 3 seconds if invalid
            }

            float currentTime = Server.CurrentTime;
            float endTime = duration == float.MaxValue ? float.MaxValue : currentTime + duration;

            if (!_playerConVars.ContainsKey(slot))
            {
                _playerConVars[slot] = new Dictionary<string, (float, float)>();
            }

            _playerConVars[slot][conVarName] = (value, endTime);

            _gravityConVar.SetValue(value);
            LogWithTimestamp($"[cs2-multifix] GRAVITY CHANGE: Set {conVarName} to {value} for slot {slot}, will reset to {_defaultGravity} after {duration:F3}s");

            // Set a direct timer to force reset the gravity
            if (duration != float.MaxValue)
            {
                _plugin.AddTimer(duration, () =>
                {
                    // Force reset gravity to 800 regardless of other conditions
                    _gravityConVar.SetValue(800f);

                    if (_playerConVars.ContainsKey(slot) &&
                        _playerConVars[slot].ContainsKey(conVarName))
                    {
                        _playerConVars[slot].Remove(conVarName);
                        if (_playerConVars[slot].Count == 0)
                        {
                            _playerConVars.Remove(slot);
                        }
                    }

                    // Very explicit logging
                    LogWithTimestamp($"[cs2-multifix] GRAVITY RESET: Timer expired after {duration:F3}s - Forced reset to 800 for slot {slot}");
                });
            }
        }

        public void ClearConVar(int slot, string conVarName)
        {
            if (conVarName != "sv_gravity") return;

            if (_playerConVars.ContainsKey(slot) && _playerConVars[slot].ContainsKey(conVarName))
            {
                _playerConVars[slot].Remove(conVarName);
                if (_playerConVars[slot].Count == 0)
                {
                    _playerConVars.Remove(slot);
                }

                _gravityConVar.SetValue(_defaultGravity);
                _wasConVarChanged["sv_gravity"] = false;

                LogWithTimestamp($"[cs2-multifix] Cleared {conVarName} for slot {slot} and reset to default {_defaultGravity}");
            }
        }

        public bool IsConVarActive(int slot, string conVarName)
        {
            if (conVarName != "sv_gravity") return false;
            if (_playerConVars.ContainsKey(slot) && _playerConVars[slot].ContainsKey(conVarName))
            {
                var (value, endTime) = _playerConVars[slot][conVarName];
                if (endTime == float.MaxValue || Server.CurrentTime <= endTime)
                {
                    return true;
                }
                LogWithTimestamp($"[cs2-multifix] ConVar {conVarName} expired for slot {slot} (current time: {Server.CurrentTime}, endTime: {endTime})");
                ClearConVar(slot, conVarName);
            }
            return false;
        }

        private int? GetSlot(CCSPlayer_MovementServices? movementServices)
        {
            uint? index = movementServices?.Pawn.Value?.Controller.Value?.Index;
            return index.HasValue ? (int)index.Value - 1 : null;
        }

        private HookResult ProcessMovementPre(DynamicHook hook)
        {
            var movementServices = hook.GetParam<CCSPlayer_MovementServices>(0);
            int? slot = GetSlot(movementServices);
            if (slot == null)
            {
                LogWithTimestamp("[cs2-multifix] ProcessMovementPre: Invalid slot");
                return HookResult.Continue;
            }

            _wasConVarChanged["sv_gravity"] = false;

            if (IsConVarActive(slot.Value, "sv_gravity"))
            {
                var (value, _) = _playerConVars[slot.Value]["sv_gravity"];
                if (_gravityConVar.GetPrimitiveValue<float>() != value)
                {
                    _gravityConVar.SetValue(value);
                    _wasConVarChanged["sv_gravity"] = true;
                    LogWithTimestamp($"[cs2-multifix] Applied sv_gravity={value} for slot {slot}");
                }
            }

            return HookResult.Continue;
        }

        private HookResult ProcessMovementPost(DynamicHook hook)
        {
            var movementServices = hook.GetParam<CCSPlayer_MovementServices>(0);
            int? slot = GetSlot(movementServices);
            if (slot == null)
            {
                return HookResult.Continue;
            }

            // Check if we need to reset gravity
            if (_playerConVars.ContainsKey(slot.Value) &&
                _playerConVars[slot.Value].ContainsKey("sv_gravity"))
            {
                var (_, endTime) = _playerConVars[slot.Value]["sv_gravity"];

                if (endTime != float.MaxValue && Server.CurrentTime > endTime)
                {
                    _gravityConVar.SetValue(800f);
                    _playerConVars[slot.Value].Remove("sv_gravity");
                    if (_playerConVars[slot.Value].Count == 0)
                    {
                        _playerConVars.Remove(slot.Value);
                    }

                    LogWithTimestamp($"[cs2-multifix] EXPIRATION CHECK: Reset gravity to 800 for slot {slot} (current: {Server.CurrentTime:F3}, end: {endTime:F3})");
                }
            }

            return HookResult.Continue;
        }

        private void LogWithTimestamp(string message)
        {
            Server.PrintToConsole($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    public class BoosterPlugin : BasePlugin
    {
        public override string ModuleName => "cs2-multifix";
        public override string ModuleAuthor => "Shizangle";
        public override string ModuleVersion => "1.0.8";

        private readonly Dictionary<int, bool> playersInNoJumpTrigger = new Dictionary<int, bool>();
        private readonly ReplicatedConVarProcessor _conVarProcessor;

        public BoosterPlugin()
        {
            _conVarProcessor = new ReplicatedConVarProcessor(this);
        }

        public override void Load(bool hotReload)
        {
            HookEntityOutput("trigger_multiple", "OnStartTouch", (CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay) =>
            {
                return HandleTriggerStartTouch(output, name, activator, caller, value, delay);
            });

            HookEntityOutput("trigger_multiple", "OnEndTouch", (CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay) =>
            {
                return HandleTriggerEndTouch(output, name, activator, caller, value, delay);
            });

            RegisterEventHandler<EventPlayerJump>((@event, info) =>
            {
                if (@event.Userid?.IsValid != true)
                {
                    LogWithTimestamp("[cs2-multifix] EventPlayerJump: Userid is null or invalid.");
                    return HookResult.Continue;
                }

                var player = @event.Userid;
                if (player.IsBot || !player.IsValid)
                {
                    return HookResult.Continue;
                }

                if (playersInNoJumpTrigger.ContainsKey(player.Slot) && playersInNoJumpTrigger[player.Slot])
                {
                    LogWithTimestamp($"[cs2-multifix] Blocked jump for {player.PlayerName} in nojump trigger.");
                    AddTimer(0.01f, () =>
                    {
                        if (player.IsValid && player.PlayerPawn.Value != null)
                        {
                            var pawn = player.PlayerPawn.Value;
                            Vector velocity = pawn.AbsVelocity;
                            velocity.Z = 0;
                            pawn.Teleport(null, null, velocity);
                        }
                    });
                    return HookResult.Handled;
                }

                return HookResult.Continue;
            });

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                _conVarProcessor.UpdateDefaultValues();
            });

            _conVarProcessor.UpdateDefaultValues();
            LogWithTimestamp("cs2-multifix loaded successfully.");
        }

        public override void Unload(bool hotReload)
        {
            _conVarProcessor.Unload();
        }

        private HookResult HandleTriggerStartTouch(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
        {
            if (caller == null || activator == null)
            {
                LogWithTimestamp("[cs2-multifix] StartTouch: Caller or activator is null");
                return HookResult.Continue;
            }

            var callerEnt = caller.Handle != 0 ? new CBaseEntity(caller.Handle) : null;
            if (callerEnt?.Entity?.DesignerName != "trigger_multiple")
            {
                LogWithTimestamp("[cs2-multifix] StartTouch: Caller is not trigger_multiple");
                return HookResult.Continue;
            }

            string entityName = callerEnt.Entity?.Name ?? "";
            var player = GetPlayerFromActivator(activator);
            if (!IsAllowedPlayer(player))
            {
                LogWithTimestamp($"[cs2-multifix] StartTouch: Invalid player for {entityName}");
                return HookResult.Continue;
            }

            LogWithTimestamp($"[cs2-multifix] StartTouch: {player.PlayerName} entered {entityName}");

            if (entityName == "nojump")
            {
                playersInNoJumpTrigger[player.Slot] = true;
                LogWithTimestamp($"[cs2-multifix] Added {player.PlayerName} to nojump trigger");
            }
            // Remove gravity handling from StartTouch completely

            return HookResult.Continue;
        }

        private HookResult HandleTriggerEndTouch(CEntityIOOutput output, string name, CEntityInstance activator, CEntityInstance caller, CVariant value, float delay)
        {
            if (caller == null || activator == null)
            {
                LogWithTimestamp("[cs2-multifix] EndTouch: Caller or activator is null");
                return HookResult.Continue;
            }

            var callerEnt = caller.Handle != 0 ? new CBaseEntity(caller.Handle) : null;
            if (callerEnt?.Entity?.DesignerName != "trigger_multiple")
            {
                LogWithTimestamp("[cs2-multifix] EndTouch: Caller is not trigger_multiple");
                return HookResult.Continue;
            }

            string entityName = callerEnt.Entity?.Name ?? "";
            var player = GetPlayerFromActivator(activator);
            if (!IsAllowedPlayer(player))
            {
                LogWithTimestamp($"[cs2-multifix] EndTouch: Invalid player for {entityName}");
                return HookResult.Continue;
            }

            LogWithTimestamp($"[cs2-multifix] EndTouch: {player.PlayerName} left {entityName}");

            if (entityName == "nojump")
            {
                playersInNoJumpTrigger.Remove(player.Slot);
                LogWithTimestamp($"[cs2-multifix] Removed {player.PlayerName} from nojump trigger");
            }
            else if (entityName.Contains("boost", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseBoostValuesFromName(entityName, out float speed, out float boostZ))
                {
                    var pawn = player.PlayerPawn.Value;
                    if (pawn != null)
                    {
                        AddTimer(0.01f, () =>
                        {
                            try
                            {
                                if (speed != 0)
                                {
                                    AdjustPlayerVelocity2D(player, speed, true);
                                }
                                Vector currentVelocity = pawn.AbsVelocity;
                                Vector newVelocity = new Vector(
                                    currentVelocity.X,
                                    currentVelocity.Y,
                                    currentVelocity.Z + boostZ
                                );
                                pawn.Teleport(null, null, newVelocity);
                                LogWithTimestamp($"{player.PlayerName} boosted via trigger! Speed:{speed}, ΔZ:{boostZ}");
                            }
                            catch (Exception ex)
                            {
                                LogWithTimestamp($"[cs2-multifix] Teleport failed: {ex.Message}");
                            }
                        });
                    }
                }
            }
            // Add gravity handling to EndTouch
            else if (entityName.Contains("gravity", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseConVarValuesFromName(entityName, out string conVarName, out float time, out float amount))
                {
                    if (conVarName == "sv_gravity")
                    {
                        _conVarProcessor.SetConVar(player.Slot, conVarName, amount, time);
                        LogWithTimestamp($"[cs2-multifix] OnEndTouch: Set {conVarName} to {amount} for {player.PlayerName} for {time}s");
                    }
                    else
                    {
                        LogWithTimestamp($"[cs2-multifix] OnEndTouch: Invalid gravity settings for {entityName}");
                    }
                }
                else
                {
                    LogWithTimestamp($"[cs2-multifix] OnEndTouch: Failed to parse gravity values for {entityName}");
                }
            }

            return HookResult.Continue;
        }

        private CCSPlayerController GetPlayerFromActivator(CEntityInstance activator)
        {
            try
            {
                return new CCSPlayerController(new CCSPlayerPawn(activator.Handle).Controller.Value!.Handle);
            }
            catch
            {
                return null;
            }
        }

        private bool ParseBoostValuesFromName(string name, out float speed, out float boostZ)
        {
            speed = boostZ = 0f;
            var parts = name.ToLower().Split('_');
            foreach (var part in parts)
            {
                if (part.StartsWith("speed") && float.TryParse(part.Substring(5), out float s))
                    speed = s;
                else if (part.StartsWith("z") && float.TryParse(part.Substring(1), out float z))
                    boostZ = z;
            }
            bool success = speed != 0 || boostZ != 0;
            LogWithTimestamp($"[cs2-multifix] Parsed '{name}' -> Speed:{speed}, Z:{boostZ}, Success:{success}");
            return success;
        }

        private bool ParseConVarValuesFromName(string name, out string conVarName, out float time, out float amount)
        {
            conVarName = "";
            time = 0f;
            amount = 0f;
            var parts = name.ToLower().Split('_');

            foreach (var part in parts)
            {
                if (part.StartsWith("gravity"))
                {
                    conVarName = "sv_gravity";
                }
                else if (part.StartsWith("time") && part.Length > 4)
                {
                    string timeStr = part.Substring(4);
                    if (float.TryParse(timeStr, out float parsedTime))
                    {
                        time = parsedTime;
                        LogWithTimestamp($"[cs2-multifix] Parsed time value: '{timeStr}' -> {time}");
                    }
                    else
                    {
                        LogWithTimestamp($"[cs2-multifix] Failed to parse time value: '{timeStr}'");
                    }
                }
                else if (part.StartsWith("amount") && part.Length > 6)
                {
                    string amountStr = part.Substring(6);
                    if (float.TryParse(amountStr, out float parsedAmount))
                    {
                        amount = parsedAmount;
                        // Add debug log
                        LogWithTimestamp($"[cs2-multifix] Parsed amount value: '{amountStr}' -> {amount}");
                    }
                    else
                    {
                        LogWithTimestamp($"[cs2-multifix] Failed to parse amount value: '{amountStr}'");
                    }
                }
            }

            if (time <= 0)
            {
                LogWithTimestamp($"[cs2-multifix] Warning: Invalid time value {time}, setting to default 3.0 seconds");
                time = 3.0f;  // Default to 3 seconds if not specified or invalid
            }

            bool success = !string.IsNullOrEmpty(conVarName) && amount > 0;
            LogWithTimestamp($"[cs2-multifix] Parsed '{name}' -> ConVar:{conVarName}, Time:{time}, Amount:{amount}, Success:{success}");
            return success;
        }

        private bool IsAllowedPlayer(CCSPlayerController? player)
        {
            return player != null && player.IsValid && !player.IsBot && player.PlayerPawn.Value != null;
        }

        private void AdjustPlayerVelocity2D(CCSPlayerController? player, float speedAdjustment, bool forceNoDebug = false)
        {
            if (!IsAllowedPlayer(player)) return;

            try
            {
                var pawn = player!.PlayerPawn.Value!;
                var currentX = pawn.AbsVelocity.X;
                var currentY = pawn.AbsVelocity.Y;

                var currentSpeedSquared = currentX * currentX + currentY * currentY;

                if (currentSpeedSquared > 0)
                {
                    var currentSpeed2D = Math.Sqrt(currentSpeedSquared);
                    var newSpeed2D = currentSpeed2D + speedAdjustment;

                    if (newSpeed2D < 0)
                    {
                        newSpeed2D = 0;
                    }

                    var normalizedX = currentX / currentSpeed2D;
                    var normalizedY = currentY / currentSpeed2D;

                    var adjustedX = normalizedX * newSpeed2D;
                    var adjustedY = normalizedY * newSpeed2D;

                    pawn.AbsVelocity.X = (float)adjustedX;
                    pawn.AbsVelocity.Y = (float)adjustedY;

                    if (!forceNoDebug)
                    {
                        LogWithTimestamp($"[cs2-multifix] Adjusted velocity for {player.PlayerName}: CurrentSpeed={currentSpeed2D:F2}, Adjustment={speedAdjustment:F2}, NewSpeed={newSpeed2D:F2}");
                    }
                }
                else
                {
                    if (!forceNoDebug)
                    {
                        LogWithTimestamp($"[cs2-multifix] Cannot adjust velocity for {player.PlayerName} because current speed is zero.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogWithTimestamp($"[cs2-multifix] Error in AdjustPlayerVelocity2D: {ex.Message}");
            }
        }

        private void LogWithTimestamp(string message)
        {
            Server.PrintToConsole($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}