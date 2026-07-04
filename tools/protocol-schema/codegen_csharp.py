"""C# code generation for the DisplayBridge protocol (schema.yaml -> Messages.cs)."""
from __future__ import annotations

CS_PRIMITIVE_TYPE = {
    "u8": "byte",
    "u16": "ushort",
    "u32": "uint",
    "u64": "ulong",
    "varint": "ulong",
    "string": "string",
    "bytes": "byte[]",
}

CS_READ_METHOD = {
    "u8": "reader.ReadByte()",
    "u16": "reader.ReadUInt16()",
    "u32": "reader.ReadUInt32()",
    "u64": "reader.ReadUInt64()",
    "varint": "ProtocolIO.ReadVarint(reader)",
    "string": "ProtocolIO.ReadString(reader)",
    "bytes": "ProtocolIO.ReadBytes(reader)",
}


def pascal(name: str) -> str:
    return name[0].upper() + name[1:]


def cs_field_type(field: dict) -> str:
    t = field["type"]
    if t == "list":
        item = field["item_type"]
        if item == "keyvalue":
            return "IReadOnlyList<ConfigEntry>"
        if item == "touch_event":
            return "IReadOnlyList<TouchEventItem>"
        return f"IReadOnlyList<{CS_PRIMITIVE_TYPE[item]}>"
    return CS_PRIMITIVE_TYPE[t]


def cs_write_stmt(field: dict, accessor: str) -> str:
    t = field["type"]
    if t == "list":
        item = field["item_type"]
        if item == "keyvalue":
            return f"ProtocolIO.WriteKeyValueList(writer, {accessor});"
        if item == "touch_event":
            return f"ProtocolIO.WriteTouchEventList(writer, {accessor});"
        return f"ProtocolIO.WriteList(writer, {accessor}, (w, v) => w.Write(v));"
    if t == "varint":
        return f"ProtocolIO.WriteVarint(writer, {accessor});"
    if t == "string":
        return f"ProtocolIO.WriteString(writer, {accessor});"
    if t == "bytes":
        return f"ProtocolIO.WriteBytes(writer, {accessor});"
    return f"writer.Write({accessor});"


def cs_read_expr(field: dict) -> str:
    t = field["type"]
    if t == "list":
        item = field["item_type"]
        if item == "keyvalue":
            return "ProtocolIO.ReadKeyValueList(reader)"
        if item == "touch_event":
            return "ProtocolIO.ReadTouchEventList(reader)"
        read = CS_READ_METHOD[item]
        return f"ProtocolIO.ReadList(reader, r => {read.replace('reader', 'r')})"
    return CS_READ_METHOD[t]


def generate_message_class(msg: dict) -> str:
    class_name = f"{msg['name']}Message"
    fields = msg["fields"]
    ctor_params = ", ".join(f"{cs_field_type(f)} {pascal(f['name'])}" for f in fields)
    lines = []
    lines.append(f"public sealed record {class_name}({ctor_params}) : IProtocolMessage")
    lines.append("{")
    lines.append(f"    public MessageType Type => MessageType.{msg['name']};")
    lines.append("")
    lines.append("    public void Serialize(BinaryWriter writer)")
    lines.append("    {")
    for f in fields:
        lines.append(f"        {cs_write_stmt(f, pascal(f['name']))}")
    lines.append("    }")
    lines.append("")
    lines.append(f"    public static {class_name} Deserialize(BinaryReader reader)")
    lines.append("    {")
    for f in fields:
        lines.append(f"        var {f['name']} = {cs_read_expr(f)};")
    args = ", ".join(f['name'] for f in fields)
    lines.append(f"        return new {class_name}({args});")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines)


CONFIG_ENTRY = """public sealed record ConfigEntry(byte Key, ulong Value)
{
    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Key);
        ProtocolIO.WriteVarint(writer, Value);
    }

    public static ConfigEntry Deserialize(BinaryReader reader)
    {
        var key = reader.ReadByte();
        var value = ProtocolIO.ReadVarint(reader);
        return new ConfigEntry(key, value);
    }
}"""

def generate_touch_event_item(messages: list[dict]) -> str:
    """TOUCH_BATCH carries a list of the same fields as the TouchEvent
    message, but as a plain struct (no MessageType/wire-type prefix) —
    mirrors the ConfigEntry pattern above."""
    touch_event = next(m for m in messages if m["name"] == "TouchEvent")
    fields = touch_event["fields"]
    ctor_params = ", ".join(f"{CS_PRIMITIVE_TYPE[f['type']]} {pascal(f['name'])}" for f in fields)
    lines = [f"public sealed record TouchEventItem({ctor_params})", "{", "    public void Serialize(BinaryWriter writer)", "    {"]
    for f in fields:
        lines.append(f"        {cs_write_stmt(f, pascal(f['name']))}")
    lines.append("    }")
    lines.append("")
    lines.append("    public static TouchEventItem Deserialize(BinaryReader reader)")
    lines.append("    {")
    for f in fields:
        lines.append(f"        var {f['name']} = {cs_read_expr(f)};")
    args = ", ".join(f['name'] for f in fields)
    lines.append(f"        return new TouchEventItem({args});")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines)


VIDEO_FRAME_HEADER = """public sealed record VideoFrameHeader(uint Len, byte Flags, ulong PtsUs)
{
    public const int WireSize = 4 + 1 + 8;

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Len);
        writer.Write(Flags);
        writer.Write(PtsUs);
    }

    public static VideoFrameHeader Deserialize(BinaryReader reader)
    {
        var len = reader.ReadUInt32();
        var flags = reader.ReadByte();
        var ptsUs = reader.ReadUInt64();
        return new VideoFrameHeader(len, flags, ptsUs);
    }
}"""

PROTOCOL_IO = """internal static class ProtocolIO
{
    public static void WriteVarint(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    public static ulong ReadVarint(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    public static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    public static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write((ushort)value.Length);
        writer.Write(value);
    }

    public static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        return reader.ReadBytes(length);
    }

    public static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> writeItem)
    {
        writer.Write((ushort)items.Count);
        foreach (var item in items) writeItem(writer, item);
    }

    public static IReadOnlyList<T> ReadList<T>(BinaryReader reader, Func<BinaryReader, T> readItem)
    {
        var count = reader.ReadUInt16();
        var result = new List<T>(count);
        for (var i = 0; i < count; i++) result.Add(readItem(reader));
        return result;
    }

    public static void WriteKeyValueList(BinaryWriter writer, IReadOnlyList<ConfigEntry> items)
    {
        writer.Write((ushort)items.Count);
        foreach (var item in items) item.Serialize(writer);
    }

    public static IReadOnlyList<ConfigEntry> ReadKeyValueList(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        var result = new List<ConfigEntry>(count);
        for (var i = 0; i < count; i++) result.Add(ConfigEntry.Deserialize(reader));
        return result;
    }

    public static void WriteTouchEventList(BinaryWriter writer, IReadOnlyList<TouchEventItem> items)
    {
        writer.Write((ushort)items.Count);
        foreach (var item in items) item.Serialize(writer);
    }

    public static IReadOnlyList<TouchEventItem> ReadTouchEventList(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        var result = new List<TouchEventItem>(count);
        for (var i = 0; i < count; i++) result.Add(TouchEventItem.Deserialize(reader));
        return result;
    }
}"""

FRAMING = """public static class MessageFraming
{
    public static void WriteFramed(BinaryWriter writer, IProtocolMessage message)
    {
        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payloadStream))
        {
            message.Serialize(payloadWriter);
        }
        var payload = payloadStream.ToArray();
        writer.Write((byte)message.Type);
        writer.Write((ushort)payload.Length);
        writer.Write(payload);
    }

    public static IProtocolMessage ReadFramed(BinaryReader reader)
    {
        var type = (MessageType)reader.ReadByte();
        var length = reader.ReadUInt16();
        var payload = reader.ReadBytes(length);
        using var payloadStream = new MemoryStream(payload);
        using var payloadReader = new BinaryReader(payloadStream);
        return type switch
        {
{FRAMING_CASES}
            _ => throw new InvalidDataException($"Unknown MessageType: {(byte)type}"),
        };
    }
}"""


HEADER_COMMENT = """// <auto-generated>
// Generated by tools/protocol-schema/generate.py from schema.yaml.
// DO NOT EDIT BY HAND — regenerate instead.
// </auto-generated>"""


def generate_csharp_files(schema: dict) -> dict[str, str]:
    """Returns {relative_filename: file_contents} for the Generated/ folder."""
    namespace = schema["meta"]["namespace_csharp"]
    message_types = schema["message_types"]
    messages = schema["messages"]

    # Enum member names must match the PascalCase message name used everywhere
    # else (message-class Type getter, framing switch: `MessageType.{msg['name']}`),
    # NOT the raw SCREAMING_CASE schema key — otherwise the generated code does
    # not compile (enum `CAPS` vs reference `MessageType.Caps`).
    type_key_to_name = {m["type"]: m["name"] for m in messages}
    enum_lines = "\n".join(
        f"    {type_key_to_name[name]} = 0x{value:02X}," for name, value in message_types.items()
    )
    message_classes = "\n\n".join(generate_message_class(m) for m in messages)

    messages_cs = f"""{HEADER_COMMENT}
#nullable enable
using System.Collections.Generic;
using System.IO;

namespace {namespace};

public enum MessageType : byte
{{
{enum_lines}
}}

public interface IProtocolMessage
{{
    MessageType Type {{ get; }}
    void Serialize(BinaryWriter writer);
}}

{message_classes}
"""

    protocol_common_cs = f"""{HEADER_COMMENT}
#nullable enable
using System.IO;

namespace {namespace};

{CONFIG_ENTRY}

{generate_touch_event_item(messages)}

{VIDEO_FRAME_HEADER}
"""

    protocol_io_cs = f"""{HEADER_COMMENT}
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace {namespace};

{PROTOCOL_IO}
"""

    framing_cases = "\n".join(
        f"            MessageType.{m['name']} => {m['name']}Message.Deserialize(payloadReader),"
        for m in messages
    )
    framing = FRAMING.replace("{FRAMING_CASES}", framing_cases)
    message_framing_cs = f"""{HEADER_COMMENT}
#nullable enable
using System.IO;

namespace {namespace};

{framing}
"""

    return {
        "Messages.cs": messages_cs,
        "ProtocolCommon.cs": protocol_common_cs,
        "ProtocolIO.cs": protocol_io_cs,
        "MessageFraming.cs": message_framing_cs,
    }
