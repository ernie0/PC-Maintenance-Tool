namespace PcMaintenance.Remote.Viewer.Decoding;

/// <summary>Media Foundation 解碼會用到的 GUID 與常數（Vortice 未提供具名常數的部分）。</summary>
internal static class Mf
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid CatVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    public const uint EnumSync = 0x1, EnumHardware = 0x4, EnumSortAndFilter = 0x40;

    public const int NeedMoreInput = unchecked((int)0xC00D6D72); // MF_E_TRANSFORM_NEED_MORE_INPUT
    public const int StreamChange = unchecked((int)0xC00D6D61);  // MF_E_TRANSFORM_STREAM_CHANGE

    // MFT_MESSAGE_TYPE
    public const int MsgBeginStreaming = 0x10000000;
    public const int MsgEndStreaming = 0x10000001;
    public const int MsgStartOfStream = 0x10000003;
    public const int MsgDrain = 1;

    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
    public static int Hi(ulong packed) => (int)(packed >> 32);
    public static int Lo(ulong packed) => (int)(packed & 0xffffffff);
}
