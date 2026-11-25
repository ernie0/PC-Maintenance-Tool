using System.Buffers.Binary;

namespace PcMaintenance.Remote.Core.Protocol;

public enum InputKind : byte
{
    MouseMove = 1,
    MouseButton = 2,
    MouseWheel = 3,
    Key = 4,
}

public enum MouseButton : byte
{
    Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5,
}

/// <summary>
/// 控制端送往被控端的一個輸入事件，編碼成緊湊的二進位（<see cref="FrameType.Input"/> 框的內容）。
/// 滑鼠座標用相對整個桌面的 0..65535 比例值，和兩端解析度無關；被控端還原成實際像素。
/// </summary>
public readonly record struct InputEvent
{
    public InputKind Kind { get; init; }
    /// <summary>MouseMove：X 比例 0..65535。</summary>
    public ushort X { get; init; }
    public ushort Y { get; init; }
    public MouseButton Button { get; init; }
    public bool IsDown { get; init; }
    public short WheelDelta { get; init; }
    public bool WheelHorizontal { get; init; }
    /// <summary>Key：鍵盤掃描碼。</summary>
    public ushort ScanCode { get; init; }
    /// <summary>Key：延伸鍵（例如右側 Ctrl、方向鍵）。</summary>
    public bool Extended { get; init; }

    public static InputEvent Move(ushort x, ushort y) => new() { Kind = InputKind.MouseMove, X = x, Y = y };
    public static InputEvent MouseDown(MouseButton b) => new() { Kind = InputKind.MouseButton, Button = b, IsDown = true };
    public static InputEvent MouseUp(MouseButton b) => new() { Kind = InputKind.MouseButton, Button = b, IsDown = false };
    public static InputEvent Wheel(short delta, bool horizontal = false) =>
        new() { Kind = InputKind.MouseWheel, WheelDelta = delta, WheelHorizontal = horizontal };
    public static InputEvent KeyDown(ushort scan, bool extended = false) =>
        new() { Kind = InputKind.Key, ScanCode = scan, IsDown = true, Extended = extended };
    public static InputEvent KeyUp(ushort scan, bool extended = false) =>
        new() { Kind = InputKind.Key, ScanCode = scan, IsDown = false, Extended = extended };

    private const int Size = 8;

    public byte[] Encode()
    {
        byte[] b = new byte[Size];
        b[0] = (byte)Kind;
        switch (Kind)
        {
            case InputKind.MouseMove:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), X);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), Y);
                break;
            case InputKind.MouseButton:
                b[1] = (byte)Button;
                b[2] = (byte)(IsDown ? 1 : 0);
                break;
            case InputKind.MouseWheel:
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), WheelDelta);
                b[3] = (byte)(WheelHorizontal ? 1 : 0);
                break;
            case InputKind.Key:
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), ScanCode);
                b[3] = (byte)(IsDown ? 1 : 0);
                b[4] = (byte)(Extended ? 1 : 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "未知的輸入事件類型");
        }
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out InputEvent ev)
    {
        ev = default;
        if (data.Length < Size) return false;
        var kind = (InputKind)data[0];
        ev = kind switch
        {
            InputKind.MouseMove => Move(
                BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(3))),
            InputKind.MouseButton when data[1] is >= 1 and <= 5 =>
                new InputEvent { Kind = kind, Button = (MouseButton)data[1], IsDown = data[2] != 0 },
            InputKind.MouseWheel => new InputEvent
            {
                Kind = kind,
                WheelDelta = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(1)),
                WheelHorizontal = data[3] != 0,
            },
            InputKind.Key => new InputEvent
            {
                Kind = kind,
                ScanCode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1)),
                IsDown = data[3] != 0,
                Extended = data[4] != 0,
            },
            _ => default,
        };
        return ev.Kind != 0;
    }
}
