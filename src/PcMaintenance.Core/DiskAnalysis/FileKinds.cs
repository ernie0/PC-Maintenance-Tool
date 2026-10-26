namespace PcMaintenance.Core.DiskAnalysis;

public enum FileKind
{
    Video,
    Picture,
    Music,
    Document,
    Archive,
    Program,
    Other,
}

public sealed record FileKindTotal(FileKind Kind, long Bytes, long Files)
{
    public string Name => FileKinds.Name(Kind);
}

/// <summary>Groups files by extension so users see what kind of content fills a folder.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    public static FileKind Classify(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var kind) ? kind : FileKind.Other;

    public static string Name(FileKind kind) => kind switch
    {
        FileKind.Video => "影片",
        FileKind.Picture => "相片與圖片",
        FileKind.Music => "音樂與錄音",
        FileKind.Document => "文件與圖面",
        FileKind.Archive => "壓縮檔與映像檔",
        FileKind.Program => "程式與安裝檔",
        _ => "其他",
    };

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string extensions)
        {
            foreach (var extension in extensions.Split(' ')) map["." + extension] = kind;
        }
        Add(FileKind.Video, "mp4 mov avi mkv wmv m4v mts m2ts ts mpg mpeg flv webm 3gp vob");
        Add(FileKind.Picture, "jpg jpeg png gif bmp tif tiff heic heif webp raw cr2 cr3 nef arw dng orf rw2 psd ai svg ico");
        Add(FileKind.Music, "mp3 wav flac aac m4a wma ogg opus aiff amr");
        Add(FileKind.Document, "pdf doc docx xls xlsx xlsm ppt pptx txt csv rtf odt ods odp md dwg dxf vsd vsdx");
        Add(FileKind.Archive, "zip rar 7z iso img tar gz tgz bz2 xz cab vhd vhdx");
        Add(FileKind.Program, "exe msi msix appx dll sys");
        return map;
    }
}
