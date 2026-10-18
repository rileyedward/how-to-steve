using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HowToSteve.Bridge;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HowToSteve.Minecraft
{
    /// <summary>
    /// Minecraft mode: toggled with a key (default B). While on, the game's own mouse-button / scroll /
    /// number-key actions are disabled, and the mouse breaks and places real Minecraft blocks through the
    /// bridge. Main thread only; driven from MainThread.PerFrame.
    /// The Input System ("new" Unity input) exposes the game's bindings as InputAction objects; disabling
    /// one stops the game's handlers from firing without patching the game.
    /// </summary>
    internal sealed class BuildController
    {
        private const float Reach = 5f;              // Unity units ≈ blocks (U2: scale 1)
        private const float PlaceRepeat = 0.25f;      // Minecraft's right-click repeat (5 ticks)

        private readonly WorldRenderer _renderer;
        private readonly McClientState _state;
        private readonly Action<string, object> _send;
        private readonly ManualLogSource _log;
        private readonly Key _toggleKey;

        private readonly List<InputAction> _disabled = new List<InputAction>();
        private bool _loggedActions;
        private GameObject _outline;

        private Target? _target;
        private float _nextPlace;
        private float _digRetryAt;

        private struct Target
        {
            public Vector3Int Block;   // Minecraft block that was hit (air for How to Fish ground)
            public int Face;           // Dirs index, in Minecraft space
            public Vector3 Cursor;     // hit point inside Block, 0..1
            public bool IsMinecraft;   // false = How to Fish ground
        }

        public BuildController(WorldRenderer renderer, McClientState state, string toggleKey, Action<string, object> send, ManualLogSource log)
        {
            _renderer = renderer;
            _state = state;
            _send = send;
            _log = log;
            if (!Enum.TryParse(toggleKey, true, out _toggleKey) || _toggleKey == Key.None) _toggleKey = Key.B;
        }

        /// <summary>Optional: block sounds and the crack-overlay textures (set after the HUD exists).</summary>
        public McSounds Sounds { get; set; }
        public Func<string, Texture2D> LoadTexture { get; set; }

        // ---------- combat: Minecraft weapons on How to Fish creatures (decisions D5) ----------

        private const float EntityReach = 3.5f;
        private float _lastAttack = -10f;
        private bool _attackHeld;
        private int? _punchDamage;

        /// <summary>Java Edition attack damage and attacks/second for held items (fist = 1 damage, 4/s).</summary>
        private static (float damage, float speed) WeaponStats(string item)
        {
            var n = item == null ? "" : item.StartsWith("minecraft:") ? item.Substring(10) : item;
            string tier = n.Split('_')[0];
            float T(float wood, float stone, float iron, float gold, float diamond, float netherite) =>
                tier == "wooden" ? wood : tier == "stone" ? stone : tier == "iron" ? iron : tier == "golden" ? gold : tier == "diamond" ? diamond : tier == "netherite" ? netherite : wood;
            if (n.EndsWith("_sword")) return (T(4, 5, 6, 4, 7, 8), 1.6f);
            if (n.EndsWith("_axe")) return (T(7, 9, 9, 7, 9, 10), tier == "iron" ? 0.9f : tier == "wooden" || tier == "stone" ? 0.8f : 1.0f);
            if (n.EndsWith("_pickaxe")) return (T(2, 3, 4, 2, 5, 6), 1.2f);
            if (n.EndsWith("_shovel")) return (T(2.5f, 3.5f, 4.5f, 2.5f, 5.5f, 6.5f), 1.0f);
            if (n.EndsWith("_hoe")) return (1, T(1, 2, 3, 1, 4, 4));
            if (n == "trident") return (9, 1.1f);
            if (n == "mace") return (6, 0.6f);
            return (1, 4f);
        }

        /// <summary>How hard How to Fish's own punch hits, so a Minecraft fist (1) equals an HTF punch.</summary>
        private int PunchDamage()
        {
            if (_punchDamage.HasValue) return _punchDamage.Value;
            var result = 0;
            try
            {
                var punching = Player.LocalPlayer?.Punching;
                if (punching != null)
                    foreach (var f in punching.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        if (f.FieldType == typeof(int) && f.Name.IndexOf("damage", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            result = (int)f.GetValue(punching);
                            _log.LogInfo($"[diag] combat: PlayerPunching.{f.Name}={result}");
                            break;
                        }
            }
            catch (Exception e) { _log.LogWarning($"combat: can't read punch damage ({e.Message})"); }
            if (result <= 0) { result = 10; _log.LogInfo("[diag] combat: punch damage not found, using 10 per Minecraft heart-half"); }
            _punchDamage = result;
            return result;
        }

        /// <summary>Left click on a How to Fish creature: hit it through the game's own hit path.</summary>
        private bool TryAttackCreature()
        {
            var cam = GameInfo.CurCamera != null ? GameInfo.CurCamera : Camera.main;
            if (cam == null) return false;
            var t = cam.transform;
            if (!Physics.Raycast(new Ray(t.position, t.forward), out var hit, EntityReach, GameInfo.ProjectileHitLayer, QueryTriggerInteraction.Ignore)) return false;
            var item = ItemManager.Get(hit.collider);
            var creature = item != null ? item.Creature : null;
            if (creature == null || creature.IsDead) return false;

            var held = _state.HotbarSlot(_state.HeldSlot)?.Item;
            var (baseDamage, speed) = WeaponStats(held);
            var now = Time.unscaledTime;
            var charge = Mathf.Clamp01((now - _lastAttack) * speed);   // Minecraft's attack cooldown
            _lastAttack = now;
            var mult = 0.2f + 0.8f * charge * charge;
            var damage = Mathf.Max(1, Mathf.RoundToInt(baseDamage * mult * PunchDamage()));
            var dir = t.forward;
            var hpBefore = creature.Hp;
            creature.LocalHit(hit.transform, hit.point, dir, Player.LocalPlayer, damage, false, dir * 4f, false, false, false);
            Swung?.Invoke();
            _log.LogInfo($"combat: hit {creature.name} with {held ?? "fist"} for {damage} (charge {charge:F2}, hp {hpBefore} -> {creature.Hp})");
            return true;
        }

        // ---------- crack overlay ----------

        private GameObject _crack;
        private MeshRenderer _crackRenderer;
        private readonly Material[] _crackMaterials = new Material[10];
        private float _nextHitSound;

        private void UpdateCrack()
        {
            var pos = _state.DigPos;
            if (pos == null || _state.DigProgress <= 0 || LoadTexture == null)
            {
                if (_crack != null && _crack.activeSelf) _crack.SetActive(false);
                return;
            }
            if (_crack == null)
            {
                _crack = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _crack.name = "HowToSteve.Crack";
                UnityEngine.Object.Destroy(_crack.GetComponent<Collider>());
                _crack.transform.SetParent(_renderer.McRoot, false);
                _crack.transform.localScale = Vector3.one * 1.004f;
                _crackRenderer = _crack.GetComponent<MeshRenderer>();
                _crackRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var stage = Mathf.Clamp(Mathf.FloorToInt(_state.DigProgress * 10f), 0, 9);
            if (_crackMaterials[stage] == null)
            {
                var tex = LoadTexture($"assets/minecraft/textures/block/destroy_stage_{stage}.png");
                var m = _renderer.MakeViewModelMaterial(new Color(1, 1, 1, 1), tex);
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.05f);
                _crackMaterials[stage] = m;
            }
            _crackRenderer.sharedMaterial = _crackMaterials[stage];
            _crack.transform.localPosition = new Vector3(pos[0] + 0.5f, pos[1] + 0.5f, pos[2] + 0.5f);
            if (!_crack.activeSelf) _crack.SetActive(true);

            if (Sounds != null && Time.unscaledTime >= _nextHitSound && _digState != null)
            {
                _nextHitSound = Time.unscaledTime + 0.25f; // vanilla plays the hit sound every 4 ticks
                Sounds.Play("step", McSounds.GroupOf(_digState), BlockCentre(pos), 0.25f, 0.5f);
            }
        }

        private string _digState;

        private Vector3 BlockCentre(int[] p) => _renderer.McRoot.TransformPoint(new Vector3(p[0] + 0.5f, p[1] + 0.5f, p[2] + 0.5f));

        public bool Enabled => _state.MinecraftMode;

        /// <summary>Raised when Steve swings (dig start, place).</summary>
        public event Action Swung;

        public void Tick(bool canBuild)
        {
            var kb = Keyboard.current;
            var player = Player.LocalPlayer;
            var inputsBlocked = player == null || SafeBlockInputs(player) || Cursor.lockState != CursorLockMode.Locked;

            if (!canBuild || !_renderer.Visible)
            {
                if (_state.MinecraftMode) SetMode(false, "overlay hidden");
                HideOutline();
                return;
            }

            if (kb != null && kb[_toggleKey].wasPressedThisFrame && !inputsBlocked) SetMode(!_state.MinecraftMode, null);
            if (!_state.MinecraftMode) { HideOutline(); return; }

            KeepGameActionsDisabled();
            _target = Raycast();
            UpdateOutline();
            if (inputsBlocked)
            {
                CancelDig();
                UpdateCrack();
                return;
            }

            HandleHotbar(kb);
            HandleDig();
            HandlePlace();
            UpdateCrack();
        }

        // ---------- mode + game input ----------

        private void SetMode(bool on, string why)
        {
            _state.MinecraftMode = on;
            if (on)
            {
                DisableGameActions();
                _state.ShowToast("Minecraft mode ON");
            }
            else
            {
                CancelDig();
                RestoreGameActions();
                HideOutline();
                UpdateCrack();
                _state.ShowToast("Minecraft mode OFF");
            }
            _log.LogInfo($"minecraft mode {(on ? "on" : "off")}{(why != null ? $" ({why})" : "")}");
        }

        private static readonly string[] BlockedPaths =
        {
            "<Mouse>/leftButton", "<Mouse>/rightButton", "<Mouse>/scroll",
            "<Keyboard>/1", "<Keyboard>/2", "<Keyboard>/3", "<Keyboard>/4", "<Keyboard>/5",
            "<Keyboard>/6", "<Keyboard>/7", "<Keyboard>/8", "<Keyboard>/9", "<Keyboard>/e",
        };

        private void DisableGameActions()
        {
            _disabled.Clear();
            var actions = GameInfo.Input?.actions;
            if (actions == null) return;
            foreach (var action in actions)
            {
                var map = action.actionMap?.name ?? "";
                if (map.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0 || map.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var hit = action.bindings.Any(b =>
                {
                    var p = b.effectivePath ?? b.path ?? "";
                    return BlockedPaths.Any(bp => p.StartsWith(bp, StringComparison.OrdinalIgnoreCase));
                });
                if (!hit || !action.enabled) continue;
                action.Disable();
                _disabled.Add(action);
            }
            if (!_loggedActions)
            {
                _loggedActions = true;
                _log.LogInfo("[diag] U7 minecraft mode disables: " + string.Join(", ", _disabled.Select(a => $"{a.actionMap?.name}/{a.name}")));
            }
        }

        private void KeepGameActionsDisabled()
        {
            // The game may re-enable its maps (pause menu, respawn); keep ours off while Minecraft mode is on.
            foreach (var a in _disabled) if (a.enabled) a.Disable();
        }

        private void RestoreGameActions()
        {
            foreach (var a in _disabled)
            {
                try { a.Enable(); } catch (Exception) { /* action gone with its asset */ }
            }
            _disabled.Clear();
        }

        private static bool SafeBlockInputs(Player p)
        {
            try { return p.BlockInputs; } catch (Exception) { return false; }
        }

        // ---------- targeting ----------

        private Target? Raycast()
        {
            var cam = GameInfo.CurCamera != null ? GameInfo.CurCamera : Camera.main;
            if (cam == null) return null;
            var t = cam.transform;
            // Island ground is on layer 8; our blocks are on the section layer (11 by default, D21).
            // The section layer also holds the island's own invisible walls, which aren't targets.
            var sectionLayer = _renderer.SectionLayer;
            var hits = Physics.RaycastAll(new Ray(t.position, t.forward), Reach, (1 << 8) | (1 << sectionLayer), QueryTriggerInteraction.Ignore);
            RaycastHit? best = null;
            var isMc = false;
            foreach (var h in hits)
            {
                if (best.HasValue && h.distance >= best.Value.distance) continue;
                var marker = h.collider.GetComponent<SectionMarker>() != null;
                if (!marker && h.collider.gameObject.layer != 8) continue;
                best = h;
                isMc = marker;
            }
            if (!best.HasValue) return null;
            var hit = best.Value;

            var root = _renderer.McRoot;
            // InverseTransformVector applies the root's (negative Z) scale; InverseTransformDirection would not.
            var n = root.InverseTransformVector(hit.normal).normalized;
            var face = Dirs.FromVector(n);
            var hp = root.InverseTransformPoint(hit.point);
            // Minecraft block: the cell just behind the hit face. How to Fish ground: the cell whose centre is
            // under the surface, the same rounding the terrain voxelizer uses, so the two agree.
            var inside = isMc ? hp - n * 0.002f : hp - n * 0.5f;
            var block = new Vector3Int(Mathf.FloorToInt(inside.x), Mathf.FloorToInt(inside.y), Mathf.FloorToInt(inside.z));
            var p = hp - (Vector3)block;
            return new Target
            {
                Block = block,
                Face = face,
                Cursor = new Vector3(Mathf.Clamp01(p.x), Mathf.Clamp01(p.y), Mathf.Clamp01(p.z)),
                IsMinecraft = isMc,
            };
        }

        private void UpdateOutline()
        {
            if (_target == null) { HideOutline(); return; }
            var t = _target.Value;
            // On How to Fish ground, outline where the block would go.
            var at = t.IsMinecraft ? t.Block : t.Block + Dirs.Vectors[t.Face];
            if (_outline == null) _outline = CreateOutline();
            _outline.transform.localPosition = at;
            if (!_outline.activeSelf) _outline.SetActive(true);
        }

        private void HideOutline()
        {
            if (_outline != null && _outline.activeSelf) _outline.SetActive(false);
        }

        private GameObject CreateOutline()
        {
            var go = new GameObject("HowToSteve.Outline") { layer = _renderer.gameObject.layer };
            go.transform.SetParent(_renderer.McRoot, false);
            const float e = 0.004f;
            float a = -e, b = 1 + e;
            var v = new List<Vector3>
            {
                new Vector3(a, a, a), new Vector3(b, a, a), new Vector3(b, a, b), new Vector3(a, a, b),
                new Vector3(a, b, a), new Vector3(b, b, a), new Vector3(b, b, b), new Vector3(a, b, b),
            };
            var idx = new[] { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            var mesh = new Mesh { name = "outline" };
            mesh.SetVertices(v);
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _renderer.MakeUnlitMaterial(new Color(0, 0, 0, 0.6f));
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        // ---------- actions ----------

        private void HandleHotbar(Keyboard kb)
        {
            var slot = _state.HeldSlot;
            var mouse = Mouse.current;
            var scroll = mouse != null ? mouse.scroll.ReadValue().y : 0f;
            if (scroll > 0.01f) slot = (slot + 8) % 9;      // wheel up = previous, like Minecraft
            else if (scroll < -0.01f) slot = (slot + 1) % 9;
            if (kb != null)
            {
                var digits = new[] { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
                for (var i = 0; i < 9; i++) if (kb[digits[i]].wasPressedThisFrame) slot = i;
            }
            if (slot == _state.HeldSlot) return;
            _state.HeldSlot = slot;
            _send(BuildTypes.SelectSlot, new SelectSlotPayload { Hotbar = slot });
        }

        private void HandleDig()
        {
            var mouse = Mouse.current;
            var held = mouse != null && mouse.leftButton.isPressed;
            if (!held) { CancelDig(); _attackHeld = false; return; }
            if (mouse.leftButton.wasPressedThisFrame && TryAttackCreature()) { CancelDig(); _attackHeld = true; return; }
            if (_attackHeld) return; // this click was an attack; don't start digging behind the creature

            var t = _target;
            if (t == null || !t.Value.IsMinecraft) { CancelDig(); return; }
            var pos = new[] { t.Value.Block.x, t.Value.Block.y, t.Value.Block.z };
            if (_state.DigPos != null && _state.DigPos.SequenceEqual(pos)) return; // already digging this one
            if (Time.unscaledTime < _digRetryAt) return;

            CancelDig();
            _state.DigPos = pos;
            _state.DigProgress = 0;
            _send(BuildTypes.Dig, new DigPayload { Action = "start", Pos = pos, Face = Dirs.Names[t.Value.Face] });
            Swung?.Invoke();
        }

        private void CancelDig()
        {
            if (_state.DigPos == null) return;
            _send(BuildTypes.Dig, new DigPayload { Action = "cancel", Pos = _state.DigPos, Face = "up" });
            _state.DigPos = null;
            _state.DigProgress = 0;
        }

        /// <summary>dig_result from the bridge (main thread).</summary>
        public void OnDigResult(DigResultPayload r)
        {
            if (r?.Pos == null || _state.DigPos == null || !_state.DigPos.SequenceEqual(r.Pos)) return;
            if (r.State != null) _digState = r.State;
            if (r.Ok == null) { _state.DigProgress = r.Progress; return; }
            if (r.Ok == true && Sounds != null) Sounds.Play("dig", McSounds.GroupOf(r.State ?? _digState), BlockCentre(r.Pos));
            _digState = null;
            _state.DigPos = null;
            _state.DigProgress = 0;
            _digRetryAt = Time.unscaledTime + 0.25f; // Minecraft's 5-tick pause before the next block
            if (r.Ok == false && r.Reason != "cancelled") _state.ShowToast($"can't break: {r.Reason}");
        }

        private void HandlePlace()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var pressed = mouse.rightButton.wasPressedThisFrame;
            var repeat = mouse.rightButton.isPressed && Time.unscaledTime >= _nextPlace;
            if (!pressed && !repeat) return;
            _nextPlace = Time.unscaledTime + PlaceRepeat;
            var t = _target;
            if (t == null) return;
            var tv = t.Value;
            Swung?.Invoke();
            _send(BuildTypes.Place, new PlacePayload
            {
                Against = new[] { tv.Block.x, tv.Block.y, tv.Block.z },
                Face = Dirs.Names[tv.Face],
                Cursor = new double[] { Math.Round(tv.Cursor.x, 3), Math.Round(tv.Cursor.y, 3), Math.Round(tv.Cursor.z, 3) },
            });
        }

        /// <summary>place_result from the bridge (main thread).</summary>
        public void OnPlaceResult(PlaceResultPayload r)
        {
            if (r != null && !r.Ok && r.Reason != "opened") _state.ShowToast($"can't place: {r.Reason}");
            if (r != null && r.Ok && r.Pos != null && Sounds != null) Sounds.Play("dig", McSounds.GroupOf(r.State), BlockCentre(r.Pos), 1f, 0.8f);
        }

        public void Shutdown()
        {
            if (_state.MinecraftMode) SetMode(false, "shutdown");
        }
    }
}
