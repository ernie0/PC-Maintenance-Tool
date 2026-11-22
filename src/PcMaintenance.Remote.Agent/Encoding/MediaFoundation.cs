using Vortice.MediaFoundation;

namespace PcMaintenance.Remote.Agent.Encoding;

/// <summary>Media Foundation 會用到的 GUID 與訊息常數（Vortice 未提供具名常數的部分）。</summary>
internal static class Mf
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-98a6aa2a2f6e");
    public static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a"); // MFSampleExtension_CleanPoint
    public static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid CatVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    public const uint EnumSync = 0x1, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
    public const uint MainProfile = 77;
    public const int NeedMoreInput = unchecked((int)0xC00D6D72);

    // MFT_MESSAGE_TYPE
    public const int MsgBeginStreaming = 0x10000000;
    public const int MsgEndStreaming = 0x10000001;
    public const int MsgDrain = 1;
    public const int MsgStartOfStream = 0x10000003;

    // METransform 事件
    public const int NeedInput = 601;
    public const int HaveOutput = 602;
    public const int DrainComplete = 603;

    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;
}
