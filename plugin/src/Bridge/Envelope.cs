using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HowToSteve.Bridge
{
    /// <summary>Wire envelope, protocol.md §2. Mirrors bridge/src/protocol.ts.</summary>
    internal sealed class Envelope
    {
        public const int ProtocolVersion = 0;

        [JsonProperty("v")] public int V { get; set; } = ProtocolVersion;
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("seq")] public long Seq { get; set; }
        [JsonProperty("ts")] public long Ts { get; set; }
        [JsonProperty("re", NullValueHandling = NullValueHandling.Include)] public long? Re { get; set; }
        [JsonProperty("payload")] public JObject Payload { get; set; } = new JObject();

        public T PayloadAs<T>() => Payload == null ? default : Payload.ToObject<T>();
    }

    internal static class MessageTypes
    {
        public const string Hello = "hello";
        public const string HelloAck = "hello_ack";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Error = "error";
        public const string McStatus = "mc_status";
        public const string PlayerState = "player_state";
        public const string BlocksNear = "blocks_near";
        public const string BlocksRequest = "blocks_request";
    }

    internal static class Features
    {
        public const string PlayerState = "player_state";
        public const string BlocksNear = "blocks_near";
        public const string Dig = "dig";
        public const string Place = "place";
        public const string Inventory = "inventory";
        public const string HudState = "hud_state";
    }

    internal static class BuildTypes
    {
        public const string Dig = "dig";
        public const string DigResult = "dig_result";
        public const string Place = "place";
        public const string PlaceResult = "place_result";
        public const string Inventory = "inventory";
        public const string SelectSlot = "select_slot";
        public const string HudState = "hud_state";
    }

    internal sealed class DigPayload
    {
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("pos")] public int[] Pos { get; set; }
        [JsonProperty("face")] public string Face { get; set; }
    }

    internal sealed class DigResultPayload
    {
        [JsonProperty("pos")] public int[] Pos { get; set; }
        [JsonProperty("ok")] public bool? Ok { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("progress")] public float Progress { get; set; }
        [JsonProperty("state")] public string State { get; set; }
    }

    internal sealed class PlacePayload
    {
        [JsonProperty("against")] public int[] Against { get; set; }
        [JsonProperty("face")] public string Face { get; set; }
        [JsonProperty("cursor")] public double[] Cursor { get; set; }
        [JsonProperty("hand")] public string Hand { get; set; } = "main";
    }

    internal sealed class PlaceResultPayload
    {
        [JsonProperty("pos")] public int[] Pos { get; set; }
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    internal sealed class InventorySlotDto
    {
        [JsonProperty("slot")] public int Slot { get; set; }
        [JsonProperty("item")] public string Item { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
    }

    internal sealed class InventoryPayload
    {
        [JsonProperty("heldSlot")] public int HeldSlot { get; set; }
        [JsonProperty("slots")] public List<InventorySlotDto> Slots { get; set; }
        [JsonProperty("cursor")] public InventorySlotDto Cursor { get; set; }
    }

    internal sealed class TerrainUploadPayload
    {
        [JsonProperty("region")] public string Region { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("origin")] public int[] Origin { get; set; }
        [JsonProperty("size")] public int[] Size { get; set; }
        [JsonProperty("encoding")] public string Encoding { get; set; }
        [JsonProperty("data")] public string Data { get; set; }
        [JsonProperty("final")] public bool Final { get; set; }
    }

    internal sealed class TerrainAckPayload
    {
        [JsonProperty("region")] public string Region { get; set; }
        [JsonProperty("revision")] public int Revision { get; set; }
        [JsonProperty("applied")] public int Applied { get; set; }
        [JsonProperty("skipped")] public int Skipped { get; set; }
    }

    /// <summary>window_open / window_update (B→P): a container the server opened for Steve.</summary>
    internal sealed class WindowPayload
    {
        [JsonProperty("windowId")] public int WindowId { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("slots")] public List<InventorySlotDto> Slots { get; set; }
        [JsonProperty("cursor")] public InventorySlotDto Cursor { get; set; }
    }

    internal sealed class WindowIdPayload
    {
        [JsonProperty("windowId")] public int WindowId { get; set; }
    }

    internal sealed class WindowClickPayload
    {
        [JsonProperty("windowId")] public int WindowId { get; set; }
        [JsonProperty("slot")] public int Slot { get; set; }
        [JsonProperty("button")] public int Button { get; set; }
        [JsonProperty("mode")] public int Mode { get; set; }
    }

    internal sealed class SelectSlotPayload
    {
        [JsonProperty("hotbar")] public int Hotbar { get; set; }
    }

    internal sealed class HudStatePayload
    {
        [JsonProperty("health")] public float Health { get; set; }
        [JsonProperty("food")] public int Food { get; set; }
        [JsonProperty("saturation")] public float Saturation { get; set; }
        [JsonProperty("xpLevel")] public int XpLevel { get; set; }
        [JsonProperty("xpProgress")] public float XpProgress { get; set; }
        [JsonProperty("air")] public int Air { get; set; }
        [JsonProperty("armor")] public int Armor { get; set; }
        [JsonProperty("gameMode")] public string GameMode { get; set; }
        [JsonProperty("dead")] public bool Dead { get; set; }
    }

    internal sealed class McStatusPayload
    {
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    internal sealed class CameraDto
    {
        [JsonProperty("pos")] public double[] Pos { get; set; }
        [JsonProperty("yaw")] public double Yaw { get; set; }
        [JsonProperty("pitch")] public double Pitch { get; set; }
        [JsonProperty("fov")] public double Fov { get; set; }
    }

    internal sealed class PlayerFlagsDto
    {
        [JsonProperty("sprinting")] public bool Sprinting { get; set; }
        [JsonProperty("sneaking")] public bool Sneaking { get; set; }
        [JsonProperty("swimming")] public bool Swimming { get; set; }
        [JsonProperty("onBoat")] public bool OnBoat { get; set; }
    }

    internal sealed class PlayerStatePayload
    {
        [JsonProperty("pos")] public double[] Pos { get; set; }
        [JsonProperty("yaw")] public double Yaw { get; set; }
        [JsonProperty("pitch")] public double Pitch { get; set; }
        [JsonProperty("vel")] public double[] Vel { get; set; }
        [JsonProperty("onGround")] public bool OnGround { get; set; }
        [JsonProperty("camera")] public CameraDto Camera { get; set; }
        [JsonProperty("flags")] public PlayerFlagsDto Flags { get; set; }
    }

    /// <summary>blocks_near (protocol §4.3): kind = section | update | unload.</summary>
    internal sealed class BlocksNearPayload
    {
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("section")] public int[] Section { get; set; }
        [JsonProperty("palette")] public string[] Palette { get; set; }
        [JsonProperty("data")] public string Data { get; set; }
        [JsonProperty("pos")] public int[] Pos { get; set; }
        [JsonProperty("state")] public string State { get; set; }
    }

    internal sealed class BlocksRequestPayload
    {
        [JsonProperty("sections")] public List<int[]> Sections { get; set; }
    }

    internal sealed class TransformDto
    {
        [JsonProperty("mcOrigin")] public double[] McOrigin { get; set; } = { 0, 64, 0 };
        [JsonProperty("scale")] public double Scale { get; set; } = 1.0;
        [JsonProperty("flipZ")] public bool FlipZ { get; set; } = true;
    }

    internal sealed class HelloPayload
    {
        [JsonProperty("plugin")] public string Plugin { get; set; } = "howtosteve";
        [JsonProperty("pluginVersion")] public string PluginVersion { get; set; }
        [JsonProperty("game")] public string Game { get; set; } = "How to Fish";
        [JsonProperty("unityVersion")] public string UnityVersion { get; set; }
        [JsonProperty("transform")] public TransformDto Transform { get; set; } = new TransformDto();
        [JsonProperty("features")] public List<string> Features { get; set; } = new List<string>();
    }

    internal sealed class McInfo
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("username")] public string Username { get; set; }
        [JsonProperty("spawned")] public bool Spawned { get; set; }
    }

    internal sealed class HelloAckPayload
    {
        [JsonProperty("bridgeVersion")] public string BridgeVersion { get; set; }
        [JsonProperty("mc")] public McInfo Mc { get; set; }
        [JsonProperty("features")] public List<string> Features { get; set; }
        [JsonProperty("rates")] public Dictionary<string, double> Rates { get; set; }
    }

    internal sealed class PingPayload
    {
        [JsonProperty("nonce")] public string Nonce { get; set; }
    }

    internal sealed class ErrorPayload
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("detail")] public JObject Detail { get; set; }
    }
}
