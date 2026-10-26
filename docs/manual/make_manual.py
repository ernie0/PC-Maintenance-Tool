"""Builds the user manual (DOCX) from the screenshots in ./images.

Usage: python make_manual.py --out <file.docx>
Screens are captured from the running app; update them before a new version, then rebuild.
The table of contents is filled in when the document is opened in Word (or updated by the PDF export step).
"""

import argparse
from pathlib import Path

from docx import Document
from docx.enum.style import WD_STYLE_TYPE
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.oxml import OxmlElement, parse_xml
from docx.oxml.ns import nsdecls, qn
from docx.shared import Cm, Pt, RGBColor

VERSION = "0.3.1"
RELEASE_MONTH = "2026 年 10 月"
IMAGES = Path(__file__).resolve().parent / "images"
CJK_FONT = "Microsoft JhengHei"
LATIN_FONT = "Segoe UI"
TEXT = RGBColor(0x1F, 0x23, 0x28)
SUBTLE = RGBColor(0x57, 0x60, 0x6A)
ACCENT = RGBColor(0x25, 0x63, 0xEB)

figure_number = 0


def set_fonts(style, size=None, bold=None, color=None):
    style.font.name = LATIN_FONT
    if size is not None:
        style.font.size = Pt(size)
    if bold is not None:
        style.font.bold = bold
    if color is not None:
        style.font.color.rgb = color
    rpr = style.element.get_or_add_rPr()
    fonts = rpr.find(qn("w:rFonts"))
    if fonts is None:
        fonts = OxmlElement("w:rFonts")
        rpr.append(fonts)
    # Theme fonts would override the explicit ones.
    for attribute in ("w:asciiTheme", "w:hAnsiTheme", "w:eastAsiaTheme", "w:cstheme"):
        fonts.attrib.pop(qn(attribute), None)
    for attribute in ("w:ascii", "w:hAnsi", "w:cs"):
        fonts.set(qn(attribute), LATIN_FONT)
    fonts.set(qn("w:eastAsia"), CJK_FONT)


def add_field(paragraph, instruction, placeholder=""):
    def char(kind):
        element = OxmlElement("w:fldChar")
        element.set(qn("w:fldCharType"), kind)
        return element

    paragraph.add_run()._r.append(char("begin"))
    code = OxmlElement("w:instrText")
    code.set(qn("xml:space"), "preserve")
    code.text = f" {instruction} "
    paragraph.add_run()._r.append(code)
    paragraph.add_run()._r.append(char("separate"))
    paragraph.add_run(placeholder)
    paragraph.add_run()._r.append(char("end"))


def shade(cell, fill):
    properties = cell._tc.get_or_add_tcPr()
    shading = OxmlElement("w:shd")
    shading.set(qn("w:val"), "clear")
    shading.set(qn("w:color"), "auto")
    shading.set(qn("w:fill"), fill)
    properties.append(shading)


def heading(doc, text, level):
    doc.add_heading(text, level=level)


def para(doc, text, style=None):
    return doc.add_paragraph(text, style=style)


def bullets(doc, items):
    for item in items:
        doc.add_paragraph(item, style="List Bullet")


def steps(doc, items):
    for number, item in enumerate(items, 1):
        paragraph = doc.add_paragraph()
        paragraph.paragraph_format.left_indent = Cm(0.9)
        paragraph.paragraph_format.first_line_indent = Cm(-0.7)
        marker = paragraph.add_run(f"{number}.　")
        marker.bold = True
        marker.font.color.rgb = ACCENT
        paragraph.add_run(item)


def tight(paragraph):
    paragraph.paragraph_format.space_after = Pt(1)
    paragraph.paragraph_format.line_spacing = 1.1


def note(doc, text, title="提示", fill="FFF4D6"):
    table = doc.add_table(rows=1, cols=1)
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    cell = table.rows[0].cells[0]
    shade(cell, fill)
    cell.width = Cm(16.5)
    paragraph = cell.paragraphs[0]
    tight(paragraph)
    label = paragraph.add_run(f"{title}　")
    label.bold = True
    paragraph.add_run(text)
    doc.add_paragraph()


def table(doc, header, rows, widths):
    grid = doc.add_table(rows=1, cols=len(header))
    grid.style = "Table Grid"
    grid.alignment = WD_TABLE_ALIGNMENT.CENTER
    for index, text in enumerate(header):
        cell = grid.rows[0].cells[index]
        cell.text = ""
        run = cell.paragraphs[0].add_run(text)
        run.bold = True
        shade(cell, "EEF2F7")
    for row in rows:
        cells = grid.add_row().cells
        for index, text in enumerate(row):
            cells[index].text = text
    for row in grid.rows:
        for index, width in enumerate(widths):
            row.cells[index].width = Cm(width)
            for paragraph in row.cells[index].paragraphs:
                tight(paragraph)
    doc.add_paragraph()


def figure(doc, file, caption, width=16.5):
    global figure_number
    figure_number += 1
    picture = doc.add_picture(str(IMAGES / file), width=Cm(width))
    # A thin frame keeps the window's light edges apart from the white page.
    picture._inline.graphic.graphicData.pic.spPr.append(
        parse_xml(f'<a:ln {nsdecls("a")} w="9525"><a:solidFill><a:srgbClr val="C9D1D9"/></a:solidFill></a:ln>'))
    doc.paragraphs[-1].alignment = WD_ALIGN_PARAGRAPH.CENTER
    label = doc.add_paragraph(f"圖 {figure_number}　{caption}", style="Caption")
    label.alignment = WD_ALIGN_PARAGRAPH.CENTER


def page_break(doc):
    doc.add_paragraph().add_run().add_break(WD_BREAK.PAGE)


def setup(doc):
    section = doc.sections[0]
    section.page_width, section.page_height = Cm(21), Cm(29.7)
    for side in ("left_margin", "right_margin"):
        setattr(section, side, Cm(2.2))
    section.top_margin = section.bottom_margin = Cm(2)
    styles = doc.styles
    set_fonts(styles["Normal"], size=11, color=TEXT)
    styles["Normal"].paragraph_format.space_after = Pt(4)
    styles["Normal"].paragraph_format.line_spacing = 1.2
    # Word fills the table of contents with these built-in styles; tight spacing keeps it on one page.
    # Added as custom styles, Word would replace them with its own definitions when it updates the table.
    for name, indent in (("toc 1", 0), ("toc 2", 0.8)):
        try:
            toc = styles[name]
        except KeyError:
            toc = styles.add_style(name, WD_STYLE_TYPE.PARAGRAPH, builtin=True)
            toc.base_style = styles["Normal"]
        toc.paragraph_format.space_after = Pt(1)
        toc.paragraph_format.line_spacing = 1.0
        toc.paragraph_format.left_indent = Cm(indent)
    for name, size in (("Title", 30), ("Heading 1", 18), ("Heading 2", 14), ("Heading 3", 12)):
        set_fonts(styles[name], size=size, bold=True, color=TEXT)
    styles["Heading 1"].paragraph_format.space_before = Pt(18)
    styles["Heading 1"].paragraph_format.page_break_before = True
    styles["Heading 2"].paragraph_format.space_before = Pt(14)
    set_fonts(styles["Caption"], size=9, bold=False, color=SUBTLE)
    set_fonts(styles["List Bullet"], size=11, color=TEXT)
    footer = section.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    footer.add_run(f"電腦維護工具使用說明書 {VERSION}　第 ").font.color.rgb = SUBTLE
    add_field(footer, "PAGE", "1")
    footer.add_run(" 頁").font.color.rgb = SUBTLE


def cover(doc):
    for _ in range(6):
        doc.add_paragraph()
    title = doc.add_paragraph("電腦維護工具", style="Title")
    title.alignment = WD_ALIGN_PARAGRAPH.CENTER
    subtitle = doc.add_paragraph()
    subtitle.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = subtitle.add_run("使用說明書")
    run.font.size = Pt(20)
    run.font.color.rgb = SUBTLE
    for _ in range(4):
        doc.add_paragraph()
    for line in (f"版本 {VERSION}", RELEASE_MONTH, "適用 Windows 10／11（64 位元）"):
        info = doc.add_paragraph(line)
        info.alignment = WD_ALIGN_PARAGRAPH.CENTER
    page_break(doc)
    contents = doc.add_paragraph()
    run = contents.add_run("目錄")
    run.bold = True
    run.font.size = Pt(18)
    add_field(doc.add_paragraph(), 'TOC \\o "1-2" \\h \\z \\u', "（在 Word 中按右鍵「更新功能變數」即可產生目錄）")


def introduction(doc):
    heading(doc, "1　這個工具能做什麼", 1)
    para(doc, "電腦維護工具是公司電腦用的 Windows 維護程式，取代 Glary Utilities 中常用的功能（包含一鍵維護），"
              "並整合 Windows 工作管理員的常用功能，以及 Wintoys 一類工具的系統調校與修復功能。"
              "程式不含廣告、不連上網路、不收集任何使用資料。")
    table(doc, ["功能", "用途"], [
        ["一鍵維護", "一次檢查暫存檔、無效捷徑、無效的解除安裝項目、使用痕跡、開機程式、磁碟、防毒與重新開機狀態，確認清單後一鍵處理"],
        ["垃圾檔清理", "清掉暫存檔、瀏覽器快取、當機傾印檔與錯誤報告等可重建的檔案；可設定定期自動清理"],
        ["開機與背景", "停用不需要隨開機執行的程式、排程工作與服務；服務也可以立即啟動或停止"],
        ["工作管理員", "查看執行中的程式與 CPU、記憶體、磁碟、網路、顯示卡的即時效能；結束沒有回應的程式"],
        ["軟體解除安裝", "執行程式本身的解除安裝程式，並清掉留下的資料夾與登錄機碼；也可以移除 Windows 內建與商店 App"],
        ["磁碟分析", "找出佔用空間的資料夾、檔案類型、大檔案、內容完全相同的重複檔與空資料夾"],
        ["磁碟工具", "查看磁碟健康狀態，檢查磁碟錯誤、最佳化磁碟、排定開機時修復"],
        ["記憶體", "查看記憶體用量與最占記憶體的程式，釋放記憶體、排定記憶體檢測"],
        ["系統調校", "常用的 Windows 效能、檔案總管與隱私設定開關，以及電源計畫"],
        ["系統工具", "系統檔案檢查、修復 Windows 映像、重設 Windows 更新與網路、備份驅動程式、清除 DNS 快取等修復，以及 Windows 內建管理工具"],
        ["電腦資訊", "Windows 版本與硬體配備，可以複製或匯出"],
        ["變更紀錄", "以上做過的系統變更都會記錄，可以還原"],
    ], [3.2, 13.3])
    heading(doc, "安全原則", 2)
    bullets(doc, [
        "一律先列出內容、由使用者確認後才執行。「一鍵維護」也是先掃描、列出清單，確認後才處理，而且只處理可以還原或會自動重建的項目；"
        "開機程式、磁碟、防毒與重新開機只提供建議。",
        "暫存檔與錯誤報告要超過 24 小時沒有變動才清理；刪除前會再檢查一次檔案沒有變動。",
        "殘留資料夾、重複檔、空資料夾與無效捷徑是移到資源回收筒；登錄機碼刪除前會先備份；開機項目、服務與系統調校的變更都能在「變更紀錄」還原。",
        "重複檔每一組至少保留一份；Microsoft 的服務不提供變更、啟動或停止；Windows 必要的處理程序不提供結束；"
        "Microsoft Store、語言套件、開始功能表等 Windows 必要的元件不會出現在 App 清單中。",
        "平常以一般權限執行。需要系統管理員的項目會標示出來，按畫面上方的「以系統管理員重新啟動」才會使用；"
        "「系統工具」中的修復則由 Windows 另外詢問權限。",
    ])


def installation(doc):
    heading(doc, "2　安裝、更新與部署（資訊人員）", 1)
    heading(doc, "2.1　手動安裝", 2)
    para(doc, "安裝檔有兩種，裝好的程式與功能相同，差別只在 .NET 執行環境；兩種可以互相升級取代。")
    table(doc, ["安裝檔", "大小", "適用"], [
        [f"PcMaintenance-Setup-{VERSION}.exe（完整版）", "約 43 MB", "任何電腦；內含執行環境，不需要網路"],
        [f"PcMaintenance-Setup-{VERSION}-lite.exe（精簡版）", "約 4 MB", "已有 .NET 10 桌面執行環境的電腦；沒有時會先從 Microsoft 下載安裝（約 58 MB，需要網路）"],
    ], [6.5, 2, 8])
    steps(doc, [
        "確認安裝檔與旁邊的 .sha256 檢查碼相符（PowerShell：Get-FileHash 安裝檔路徑）。",
        "執行安裝檔，Windows 詢問權限時按「是」。",
        "依畫面選擇是否「建立桌面捷徑」與「自動清理」（兩者預設都不勾），完成安裝。",
        "從開始功能表開啟「電腦維護工具」，左下角應顯示版本 " + VERSION + "。",
    ])
    para(doc, "精簡版裝在沒有 .NET 10 桌面執行環境的電腦上時，「準備安裝」頁會先註明要下載；按「安裝」後依序顯示下載進度與安裝執行環境的畫面，"
              "完成後才安裝電腦維護工具。安裝執行環境可能需要幾分鐘，期間進度條會來回移動。下載的是 Microsoft 官方檔案，安裝檔會核對檢查碼，不符就不執行。"
              "解除安裝電腦維護工具時不會移除這個執行環境，因為其他程式也可能用到。")
    figure(doc, "00-安裝-精簡版安裝執行環境.png", "精簡版安裝檔下載完成後，正在安裝 .NET 10 桌面執行環境", width=11)
    note(doc, "程式與安裝檔沒有數位簽章。從網路下載的副本可能出現「Windows 已保護您的電腦」，確認檢查碼相符後按「其他資訊」→「仍要執行」。")
    heading(doc, "2.2　靜默安裝（派送工具或登入指令碼）", 2)
    para(doc, f"PcMaintenance-Setup-{VERSION}.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SETTINGS=\"\\\\伺服器\\共用\\settings.json\" /TASKS=\"autoclean\"")
    table(doc, ["參數", "用途"], [
        ["/SETTINGS=\"路徑\"", "把公司設定檔複製到 C:\\ProgramData\\PcMaintenance\\settings.json。相對路徑以安裝檔所在資料夾為準；找不到檔案時安裝中止（結束代碼 7）"],
        ["/TASKS=\"autoclean\"", "開啟自動清理；同時建立桌面捷徑寫成 /TASKS=\"autoclean,desktopicon\""],
        ["/TASKS=\"desktopicon\"", "建立桌面捷徑"],
        ["/LOG=\"記錄檔路徑\"", "留下安裝記錄"],
    ], [4.5, 12])
    para(doc, "精簡版用同樣的參數；沒有執行環境的電腦在靜默安裝時也會自動下載安裝。"
              "安裝檔的結束代碼：0 完成；7 安裝前停止（找不到 /SETTINGS 的檔案，或精簡版無法下載或安裝執行環境，原因寫在 /LOG 的記錄檔）；"
              "其他非 0 值表示沒有完成。不能連外的電腦請用完整版，或先派送 Microsoft 的 .NET 10 桌面執行環境安裝檔"
              "（windowsdesktop-runtime-10.0.<n>-win-x64.exe /install /quiet /norestart）。")
    para(doc, "靜默解除安裝：\"C:\\Program Files\\PcMaintenance\\unins000.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
    heading(doc, "2.3　公司設定檔", 2)
    para(doc, "程式依序讀取第一個存在的設定檔：C:\\ProgramData\\PcMaintenance\\settings.json（建議，全公司共用），其次是程式資料夾內的 settings.json；"
              "都沒有就用預設值。安裝後在 C:\\ProgramData\\PcMaintenance 有一份 settings.example.json，內容等於預設值，複製成 settings.json 再修改即可。"
              "檔案可以寫 // 註解；格式錯誤時程式改用預設值並在畫面上方提醒。")
    table(doc, ["欄位", "預設", "說明"], [
        ["minFileAgeHours", "24", "暫存檔與錯誤報告至少放幾小時才清理"],
        ["disabledJunkCategories", "[]", "不讓使用者清理的項目代碼（代碼見附錄 A）"],
        ["excludedPaths", "[]", "永遠不刪除的資料夾，例如 \"D:\\\\公司資料\""],
        ["showMicrosoftItems", "false", "「開機與背景」是否預設列出 Microsoft 項目"],
        ["allowServiceChanges", "true", "是否允許變更服務的啟動類型與啟動、停止"],
        ["duplicateMinSizeMB", "1", "搜尋重複檔時預設的最小檔案大小（MB）"],
        ["autoCleanIntervalDays", "7", "自動清理的最短間隔天數；0 表示每次登入都清理"],
    ], [4.5, 1.6, 10.4])
    heading(doc, "2.4　更新到新版本", 2)
    table(doc, ["項目", "說明"], [
        ["更新方式", "直接執行新版安裝檔（手動或靜默皆可），不需先解除安裝；執行中的電腦維護工具會被自動關閉"],
        ["兩種安裝檔", "完整版與精簡版可以互相換裝，裝在同一個資料夾；從完整版換成精簡版時，完整版自帶的執行環境檔案會自動移除"],
        ["保留的資料", "公司設定檔、每位使用者的變更紀錄、登錄備份與記錄檔都會保留；自動清理沿用上次安裝的選擇（靜默安裝時以 /TASKS 為準）"],
        ["備份", "不需要；要保險可先複製 %LOCALAPPDATA%\\PcMaintenance"],
        ["停機", "不需要重新開機；更新時程式需關閉約 1 分鐘"],
        ["驗證", "開啟程式，左下角版本應為新版；有開自動清理的電腦，下次登入後「垃圾檔清理」頁下方會顯示上次自動清理時間"],
        ["授權", "本工具沒有授權機制，不需序號"],
    ], [3, 13.5])
    heading(doc, "2.5　版本更新內容", 2)
    bullets(doc, [
        "0.3.1：一鍵維護執行時跳出進度小視窗，顯示正在做哪一項、實際讀取或處理了多少，並有小動畫；"
        "建立還原點等待 Windows 詢問權限時，畫面不再停住；系統還原需要的服務被停用時，會直接說明原因。"
        "「重設 Windows 更新」不再停止 Cryptographic Services（它會連帶停止應用程式控管服務，並停在 Y/N 確認），"
        "結束時依實際結果顯示下載快取是否已改名。電源計畫與服務啟動類型的下拉選項，螢幕報讀改為讀出選項名稱；"
        "變更服務啟動類型後的狀態文字改為「已將服務啟動類型改為…」。"
        "另提供約 4 MB 的精簡版安裝檔：使用電腦上的 .NET 10 桌面執行環境，沒有時安裝前自動從 Microsoft 下載安裝。",
        "0.3.0：新增「一鍵維護」「系統調校」「系統工具」三項功能；軟體解除安裝新增「內建與商店 App」分頁，磁碟分析新增「空資料夾」分頁；"
        "垃圾檔清理新增「縮圖快取」「最近使用的項目」（預設不勾）；命令列新增 --restore-point。"
        "修正：解除安裝程式放在這個帳號無法讀取的資料夾時（例如 Intel 管理引擎），不再誤判為「找不到解除安裝程式」。"
        "介面改版：每項功能有自己的顏色與圖示，卡片、表格、按鈕、搜尋框、捲軸重新設計；"
        "圖示加入動畫（滑鼠移到功能或按鈕上時圖示會動、切換頁面時淡入、執行中顯示轉動的圓圈、完成時出現勾號）。"
        "Windows 關閉「動畫效果」或以遠端桌面連線時不播放動畫；程式閒置時不會因為動畫而占用處理器。",
        "0.2.0：新增工作管理員（處理程序與效能頁）、磁碟工具、記憶體、電腦資訊；自動清理、匯出 CSV 與命令列操作；"
        "磁碟分析的檔案類型統計；顯示卡著色器快取與系統當機傾印檔兩個清理項目；可清掉解除安裝程式已不存在的軟體殘留；服務可以立即啟動、停止、重新啟動（Microsoft 服務除外）。",
    ])
    heading(doc, "2.6　資料存放位置", 2)
    bullets(doc, [
        "程式：C:\\Program Files\\PcMaintenance",
        "公司設定：C:\\ProgramData\\PcMaintenance\\settings.json",
        "每位使用者：%LOCALAPPDATA%\\PcMaintenance（journal.json 變更紀錄、backups 登錄備份、logs 每日記錄、auto-clean.json 自動清理紀錄、"
        "maintenance.json 上次一鍵維護的結果）。解除安裝不會刪除這些資料。",
    ])


def getting_started(doc):
    heading(doc, "3　開始使用", 1)
    para(doc, "從開始功能表開啟「電腦維護工具」，第一個畫面是「一鍵維護」。左側是各項功能，點一下即可切換；右側上方是該頁的說明。")
    bullets(doc, [
        "畫面上方黃色提示列表示目前以一般權限執行：標示「需要系統管理員」的項目無法使用。需要時按「以系統管理員重新啟動」，Windows 詢問權限時按「是」。",
        "左下角顯示是否有系統管理員權限，以及目前使用的是預設值或公司設定檔。",
        "按鈕呈灰色表示目前不能執行，例如還沒選取項目、正在掃描，或需要系統管理員權限；選取項目後，畫面下方通常會說明原因。",
        "正在掃描或讀取時，按鈕上的圖示會變成轉動的圓圈；完成的項目旁會出現勾號。",
    ])


def features(doc):
    heading(doc, "4　各功能操作", 1)

    heading(doc, "4.1　一鍵維護", 2)
    figure(doc, "01-一鍵維護.png", "一鍵維護：掃描後列出每一項的結果，勾選的項目可以一鍵處理")
    steps(doc, [
        "按「一鍵掃描」，等待八個項目檢查完成（通常數十秒，暫存檔越多越久；可以按「取消」中止）。",
        "看每一項的結果：勾號表示正常；「可釋放…」「…個無效捷徑」這類結果可以一鍵處理；"
        "開機程式、磁碟、防毒與重新開機只提供建議，右側的按鈕會帶你到處理的地方。",
        "確認要處理的項目已勾選（「使用痕跡」預設不勾）；需要時勾選「處理前先建立系統還原點」。",
        "按「一鍵修復」，確認清單後按「確定」。完成後每一項顯示處理結果，上方會記錄這次處理的項數與釋放的空間。",
    ])
    para(doc, "掃描與修復時會跳出「一鍵維護進度」小視窗：環形只隨完成的項目與類別前進，不依時間推估；"
              "藍字是目前實際在做的事，例如正在掃描哪一類、已讀取多少個檔案、已釋放多少空間；下方清單逐項顯示結果。"
              "完成後約 1.6 秒自動關閉。關閉小視窗不會中止，進度也會顯示在主畫面；要中止請按「取消」。")
    figure(doc, "01-一鍵維護-進度視窗.png", "一鍵維護進度：正在掃描使用者暫存檔，顯示已讀取的檔案數", width=9)
    table(doc, ["項目", "檢查內容", "怎麼處理"], [
        ["暫存檔與快取", "與「垃圾檔清理」預設勾選的項目相同（不含資源回收筒）；1 MB 以上才列為可處理", "刪除；都是會自動重建的檔案"],
        ["無效捷徑", "桌面與開始功能表中，目標已不存在的捷徑；網路位置與無法判斷的捷徑不列入", "移到資源回收筒"],
        ["無效的解除安裝項目", "解除安裝程式與程式資料夾都已不存在、只剩清單項目的軟體", "刪除登錄項目，刪除前先備份"],
        ["使用痕跡", "檔案總管與跳躍清單「最近開啟的檔案」紀錄", "只刪紀錄，不刪檔案（預設不勾）"],
        ["開機啟動程式", "8 個以上非 Microsoft 的程式隨開機啟動", "建議：前往「開機與背景」"],
        ["磁碟空間與健康", "磁碟回報健康警告，或剩餘空間不到 10%", "建議：前往「磁碟工具」"],
        ["防毒保護", "防毒軟體沒有開啟即時保護，或病毒碼不是最新", "建議：開啟 Windows 安全性"],
        ["重新開機", "Windows 更新等待重新開機，或已連續開機 14 天以上", "建議：開啟 Windows Update"],
    ], [3.4, 8.3, 4.8])
    bullets(doc, [
        "處理過的捷徑與登錄項目都記錄在「變更紀錄」：捷徑從資源回收筒還原，登錄項目在變更紀錄還原。",
        "公用桌面、所有使用者的開始功能表與整台電腦的軟體項目需要系統管理員權限；以一般權限執行時會略過，結果中註明需要權限的數量。",
        "建立還原點需要系統管理員權限，以一般權限執行時 Windows 會詢問權限；24 小時內已建立過還原點時，Windows 會沿用前一個。"
        "系統保護未開啟時，程式會先詢問是否在沒有還原點的情況下繼續。",
    ])

    heading(doc, "4.2　垃圾檔清理", 2)
    figure(doc, "02-垃圾檔清理.png", "垃圾檔清理：掃描後列出每一項可釋放的空間")
    steps(doc, [
        "按「掃描」，等待各項目顯示可清理的大小（掃把圖示表示有可清理的檔案，勾號表示沒有）。",
        "勾選要清理的項目（資源回收筒、顯示卡著色器快取、縮圖快取與最近使用的項目預設不勾）。",
        "按「清理已勾選項目」，確認清單與大小後按「確定」。",
    ])
    note(doc, "瀏覽器開著時無法清它的快取（圖中 Chrome 顯示「正在執行，請先關閉再清理」），關閉瀏覽器後重新掃描即可。"
              "暫存檔與快取刪除後無法還原，但它們都是程式會自動重建的資料。")
    bullets(doc, [
        "縮圖快取：清掉後檔案總管會重新產生相片與影片的縮圖，第一次開資料夾可能稍慢。",
        "最近使用的項目：只刪「最近開啟的檔案」與跳躍清單紀錄，不會刪除檔案本身；清除後這些清單會是空的。",
    ])

    heading(doc, "4.3　開機與背景", 2)
    figure(doc, "03-開機與背景.png", "開機與背景：上方篩選類型，可搜尋名稱、發行者或命令")
    steps(doc, [
        "用上方的「全部／開機啟動項／排程工作／服務」篩選，或在搜尋框輸入名稱。",
        "點選一個項目，下方會顯示它的位置與執行的命令。",
        "開機啟動項與排程工作：按「停用」或「啟用」。",
        "服務：在「啟動類型」選擇自動、自動（延遲啟動）、手動或停用後按「套用」（下次開機生效）；也可以按「啟動」「停止」「重新啟動」立即生效。",
    ])
    bullets(doc, [
        "Microsoft 的項目預設隱藏；勾選「顯示 Microsoft 項目」才會列出，但 Microsoft 的服務不提供變更。",
        "停用與啟動類型的變更都會寫入「變更紀錄」，可以還原；立即啟動或停止只維持到下次開機。",
        "「匯出清單」會把所有項目（含 Microsoft 項目）存成 CSV，可用 Excel 開啟。",
    ])

    heading(doc, "4.4　工作管理員", 2)
    para(doc, "處理程序分頁每 2 秒更新一次，列出每個程式的 CPU、記憶體與執行緒數。")
    figure(doc, "04-工作管理員-處理程序.png", "工作管理員：處理程序（圖中以「Windows」搜尋）")
    steps(doc, [
        "在搜尋框輸入程式名稱或處理程序編號，或點欄位標題排序（例如依 CPU 或記憶體）。",
        "選取沒有回應的程式，按「結束工作」，確認後程式會立即關閉（未儲存的資料會遺失）。",
        "按「開啟檔案位置」可以在檔案總管中找到這個程式。",
    ])
    note(doc, "Windows 必要的處理程序（例如檔案總管、csrss、winlogon）、系統服務與其他使用者的程式不提供結束，選取時下方會說明原因。"
              "服務請到「開機與背景」管理。")
    para(doc, "效能分頁與 Windows 工作管理員相同：左側選擇項目，右側顯示最近 2 分鐘的圖表與詳細數據。")
    figure(doc, "05-工作管理員-效能.png", "工作管理員：效能（CPU）")
    table(doc, ["項目", "可以看到"], [
        ["CPU", "使用率、目前速度、處理程序與執行緒數、已開機時間、基本速度、核心與邏輯處理器、虛擬化、快取"],
        ["記憶體", "使用中、可用、已認可、已快取、分頁集區、速度、已使用的插槽、外型規格"],
        ["磁碟", "使用中時間、平均回應時間、讀寫速度、容量、是否為系統磁碟、類型（SSD 或傳統硬碟）"],
        ["網路", "傳送與接收速度、介面卡名稱、連線類型、連結速度、IP 位址、實體位址"],
        ["GPU", "使用率、專用與共用 GPU 記憶體、驅動程式版本與日期"],
    ], [2.5, 14])

    heading(doc, "4.5　軟體解除安裝", 2)
    figure(doc, "06-軟體解除安裝.png", "軟體解除安裝（圖中以「Microsoft」搜尋）")
    steps(doc, [
        "搜尋或捲動找到要移除的程式，點選後按「解除安裝」，確認後依照該程式的解除安裝畫面完成。",
        "完成後下方會列出留下的資料夾與登錄機碼；確認勾選的項目後按「移除已勾選的殘留」。",
        "如果解除安裝視窗還在進行或已取消，程式仍在清單中，完成後按「重新檢查」。",
    ])
    bullets(doc, [
        "名稱旁標示「找不到解除安裝程式」的軟體，表示程式已被手動刪除；按「解除安裝」可改為直接清掉它留下的項目（含清單中的這一筆）。",
        "殘留資料夾會移到資源回收筒；登錄機碼刪除前會先備份，可在「變更紀錄」還原。使用者資料夾與程式設定類的項目預設不勾選。",
        "「匯出清單」會把所有程式存成 CSV，檔名帶電腦名稱與日期，方便盤點各台電腦。",
    ])
    para(doc, "「內建與商店 App」分頁列出 Windows 內建與從 Microsoft Store 安裝的 App。")
    figure(doc, "07-內建與商店App.png", "內建與商店 App：按「勾選常見不需要的 App」後的畫面")
    steps(doc, [
        "切到「內建與商店 App」分頁，等待清單載入（第一次約需數秒）。",
        "按「勾選常見不需要的 App」，會勾選標示「常見不需要」的項目（例如取得協助、Bing 新聞、Solitaire、Xbox 相關 App）；也可以自行勾選或取消。",
        "按「移除已勾選的 App」，確認清單後按「確定」。",
    ])
    bullets(doc, [
        "只會為目前登入的使用者移除；同一台電腦的其他帳號不受影響。",
        "移除後可以在 Microsoft Store 搜尋名稱重新安裝；變更紀錄會記下移除了哪些 App。",
    ])

    heading(doc, "4.6　磁碟分析", 2)
    figure(doc, "08-磁碟分析-空間.png", "磁碟分析：檔案類型、資料夾大小與最大的檔案")
    steps(doc, [
        "選擇磁碟或按「瀏覽…」選資料夾，按「開始分析」。",
        "上方依檔案類型（影片、相片、文件…）列出佔用空間；左側可以逐層展開資料夾，右側是最大的檔案。",
        "選取資料夾後按「在檔案總管中開啟」，或選取檔案後按「在檔案總管中顯示」，再自行決定要搬移或刪除哪些檔案。",
    ])
    figure(doc, "09-磁碟分析-重複檔案.png", "重複檔案：每組至少保留一份，勾選的會移到資源回收筒")
    steps(doc, [
        "切到「重複檔案」分頁，選擇資料夾與最小檔案大小，按「搜尋重複檔」。",
        "內容完全相同的檔案會分組列出；按「每組保留最新的，其餘勾選」，或自行勾選要移除的副本。",
        "不確定時按該列的「開啟位置」查看，確認後按「移到資源回收筒」。",
    ])
    figure(doc, "10-磁碟分析-空資料夾.png", "空資料夾：只列出最外層的空資料夾，裡面的空子資料夾一起移除")
    steps(doc, [
        "切到「空資料夾」分頁，選擇要整理的資料夾（預設是「文件」），按「搜尋空資料夾」。",
        "搜尋結果預設都不勾選；確認後勾選要移除的項目，或按「全部勾選／取消」。",
        "按「移到資源回收筒」，確認後移除。移除前會再檢查一次資料夾裡沒有檔案。",
    ])
    note(doc, "隱藏或系統資料夾、雲端硬碟尚未下載到電腦的資料夾、名稱以「.」開頭的程式設定資料夾，以及 Windows、Program Files、ProgramData 都不會列出。"
              "有些程式會用空資料夾存放設定，不確定時先按「開啟位置」查看。")

    heading(doc, "4.7　磁碟工具", 2)
    figure(doc, "11-磁碟工具.png", "磁碟工具：每顆磁碟的健康狀態、類型與剩餘空間")
    bullets(doc, [
        "健康狀態顯示「警告」或「不良」時，請盡快備份資料並安排檢修；剩餘空間不到 10% 時用量條會變紅。",
        "「檢查錯誤」只檢查、不修改。若發現需要修復的問題，會出現「排定開機時修復」，下次開機時 Windows 會先修復這個磁碟（可能需要數分鐘到數小時，期間請勿關機）。",
        "「最佳化」：SSD 執行修剪，傳統硬碟執行重組，可以隨時取消。Windows 預設每週會自動最佳化，平常不必手動執行。",
        "這些操作與磁碟溫度、使用時數、SSD 壽命都需要系統管理員權限。",
    ])

    heading(doc, "4.8　記憶體", 2)
    figure(doc, "12-記憶體.png", "記憶體：用量、最占記憶體的程式與記憶體模組")
    bullets(doc, [
        "上方三張卡片分別是實體記憶體用量、虛擬記憶體（實體記憶體加上分頁檔）與已安裝的記憶體和插槽數；需要加裝記憶體時可以先看插槽是否還有空位。",
        "「釋放記憶體」會請程式交還暫時用不到的記憶體，不會關閉任何程式；效果是暫時的，切回程式時可能稍慢，平常不必經常使用。",
        "「記憶體檢測…」會開啟 Windows 記憶體診斷，重新開機後檢查記憶體是否故障，約需 15 到 30 分鐘。",
    ])

    heading(doc, "4.9　系統調校", 2)
    figure(doc, "13-系統調校.png", "系統調校：電源計畫，以及效能、檔案總管、隱私三組設定開關")
    bullets(doc, [
        "開關顯示的是 Windows 目前的設定。切換後立即寫入，每個開關下方註明何時生效，例如立即生效、重新整理檔案總管視窗後生效、重新開機後生效。",
        "每次切換都記錄在「變更紀錄」，可以還原成切換前的設定；再切一次開關也可以改回來。",
        "標示「需要系統管理員權限」的開關（例如快速啟動、硬體加速 GPU 排程、診斷資料）要先按上方的「以系統管理員重新啟動」。",
        "需要重新啟動檔案總管的設定，切換後上方會出現「重新啟動檔案總管」按鈕；檔案總管會關閉再開啟，已開啟的資料夾視窗會關閉。",
        "電源計畫：選擇後立即切換，也會記錄在變更紀錄。「加入「終極效能」」會加入 Windows 內建但預設隱藏的終極效能計畫，適合桌上型電腦；筆電會比較耗電。",
        "全部項目與生效時機見附錄 B。",
    ])

    heading(doc, "4.10　系統工具", 2)
    figure(doc, "14-系統工具.png", "系統工具：修復、快速處理、還原點與 Windows 內建工具")
    table(doc, ["分類", "項目"], [
        ["修復（需要系統管理員）", "系統檔案檢查（SFC）、修復 Windows 映像（DISM）、清理 Windows 更新元件、重設 Windows 更新、重設網路連線（Winsock）、備份驅動程式"],
        ["快速處理", "清除 DNS 快取、重建圖示與縮圖快取、重新啟動檔案總管、重新啟動顯示卡驅動程式、重設 Microsoft Store"],
        ["還原點", "建立還原點、系統還原、系統保護設定"],
        ["Windows 內建工具", "裝置管理員、服務、工作排程器、事件檢視器、資源監視器、可靠性監視器、磁碟管理、Windows 防火牆等捷徑；這台電腦沒有的工具不會顯示"],
    ], [4.4, 12.1])
    bullets(doc, [
        "修復項目按「執行」後，Windows 會詢問權限，並開啟命令視窗顯示進度與結果；請等視窗中的指令完成、看過訊息後再關閉。",
        "系統異常時先執行「系統檔案檢查」；回報無法修復時，執行「修復 Windows 映像」（Windows 會連到 Windows Update 下載修復檔），完成後再執行一次系統檔案檢查。",
        "「重設 Windows 更新」會停止更新服務，把下載快取資料夾改名保留（名稱後加上日期時間），不會刪除；「重設網路連線」完成後需要重新開機。",
        "會關閉視窗或畫面會閃的項目（重建圖示快取、重新啟動檔案總管、重新啟動顯示卡驅動程式等）執行前都會先確認。",
        "較新的 Windows 只允許系統管理員清除 DNS 快取，按下後若 Windows 詢問權限，按「是」即可。",
    ])

    heading(doc, "4.11　電腦資訊", 2)
    figure(doc, "15-電腦資訊.png", "電腦資訊：Windows 版本與硬體配備")
    bullets(doc, [
        "包含 Windows 版本與組建、電腦型號與序號、主機板與 BIOS、處理器、記憶體模組、顯示卡、磁碟與網路卡。",
        "「複製全部」可以貼到訊息或文件中交給維修人員。",
        "「匯出 CSV」一台電腦一列；把多台電腦的檔案合併，就是一張配備總表。",
    ])

    heading(doc, "4.12　變更紀錄", 2)
    figure(doc, "16-變更紀錄.png", "變更紀錄：選取一筆後按「還原選取的變更」")
    bullets(doc, [
        "停用開機項目、變更服務啟動類型、系統調校的開關與電源計畫、刪除殘留登錄機碼，以及移除的資料夾、捷徑與 App 都會記錄在這裡。",
        "選取一筆後按「還原選取的變更」即可還原；資料夾與捷徑請從資源回收筒還原，App 請從 Microsoft Store 重新安裝（狀態欄會註明）。",
        "還原排程工作、服務、「所有使用者」範圍的項目，以及需要系統管理員的系統設定時，需要系統管理員權限。",
        "「開啟記錄與備份資料夾」可查看每日記錄與登錄備份檔。",
    ])


def command_line(doc):
    heading(doc, "5　命令列與自動化（資訊人員）", 1)
    para(doc, "以下參數不開視窗，結果寫在 %LOCALAPPDATA%\\PcMaintenance\\logs 的每日記錄檔，適合派送工具或排程使用。")
    table(doc, ["參數", "用途"], [
        ["--clean", "立即清理預設勾選的項目"],
        ["--clean --dry-run", "只列出會清理的內容，不刪除任何檔案"],
        ["--auto-clean", "距離上次自動清理超過設定天數才清理（安裝時勾選「自動清理」就是在每次登入執行這一項）"],
        ["--export-software <檔案或資料夾>", "匯出軟體清單 CSV"],
        ["--export-startup <檔案或資料夾>", "匯出開機與背景項目 CSV"],
        ["--export-system <檔案或資料夾>", "匯出電腦配備與系統版本 CSV（一台一列）"],
        ["--restore-point", "建立系統還原點（需要以系統管理員執行）"],
    ], [5.5, 11])
    para(doc, "給資料夾時，檔名會自動帶電腦名稱與日期。結束代碼：0 成功（含未到時間而略過）、1 執行失敗、2 參數錯誤；"
              "--restore-point 另有 3（24 小時內已有還原點，Windows 略過）與 4（系統保護未開啟）。")
    para(doc, "範例（收集各台電腦的軟體清單）：")
    para(doc, "\"C:\\Program Files\\PcMaintenance\\PcMaintenance.exe\" --export-software \"\\\\伺服器\\共用\\軟體盤點\\\"")
    heading(doc, "自動清理的範圍", 2)
    bullets(doc, [
        "只清理畫面上預設勾選、且不需要系統管理員權限的項目；不清空資源回收筒。",
        "瀏覽器正在執行時略過它的快取，不會關閉任何程式；在背景以低優先權執行。",
        "要關閉：重新安裝時不勾選，或在「開機與背景」停用名為 PcMaintenanceAutoClean 的開機啟動項。",
    ])


def faq(doc):
    heading(doc, "6　常見問題", 1)
    for question, answer in (
        ("按鈕是灰色的，不能按？", "可能還沒選取項目、正在執行其他工作，或需要系統管理員權限。選取項目後畫面下方會說明原因；需要權限時按上方的「以系統管理員重新啟動」。"),
        ("為什麼瀏覽器的快取沒有清掉？", "瀏覽器開著時檔案正在使用，程式會略過。關閉瀏覽器後重新掃描、清理。"),
        ("不小心移除了東西怎麼辦？", "資料夾、重複檔、空資料夾與捷徑在資源回收筒；開機項目、服務啟動類型、系統調校與登錄機碼可在「變更紀錄」還原；"
                                "內建 App 可以從 Microsoft Store 重新安裝。暫存檔與快取無法還原，但它們都是會自動重建的資料。"),
        ("「一鍵維護」會刪掉我的檔案嗎？", "不會刪除個人檔案。它只刪除暫存檔、快取與「最近開啟」紀錄（都是會自動重建的資料），無效捷徑移到資源回收筒，"
                                   "無效的軟體清單項目刪除前會先備份；處理前一定先列出清單讓你確認。"),
        ("系統調校的開關切換後想改回來？", "再切一次開關即可；或到「變更紀錄」選取那一筆，按「還原選取的變更」，會恢復成切換前的設定。"),
        ("按「清除 DNS 快取」時 Windows 詢問權限？", "較新的 Windows 只允許系統管理員清除 DNS 快取，按「是」即可；按「否」則不會清除。"),
        ("解除安裝後還看得到這個程式？", "有些解除安裝程式會在背景繼續執行，完成後按「重新檢查」。"),
        ("這個工具會連上網路或回傳資料嗎？", "不會。程式沒有任何連網功能，也不收集使用資料。"
                                    "（「修復 Windows 映像」是由 Windows 自己連到 Windows Update 下載修復檔；"
                                    "精簡版安裝檔在電腦沒有 .NET 10 時，安裝過程會從 Microsoft 下載執行環境。）"),
        ("精簡版安裝時顯示「無法從 Microsoft 下載 .NET 10 桌面執行環境」？", "這台電腦連不到 Microsoft 的下載站（builds.dotnet.microsoft.com）。"
                                    "確認網路或 Proxy 後再執行一次；不能連外的電腦請改用完整版安裝檔。沒有裝上任何東西，不需要清理。"),
        ("磁碟檢查發現問題怎麼辦？", "先備份重要資料，再按「排定開機時修復」並重新開機。健康狀態顯示警告或不良時，請安排更換磁碟。"),
        ("圖示的動畫可以關掉嗎？", "可以。本工具跟隨 Windows 的動畫設定：Windows 11 在「設定 → 協助工具 → 視覺效果」關閉「動畫效果」；"
                              "Windows 10 在「設定 → 輕鬆存取 → 顯示」關閉「在 Windows 中顯示動畫」。關閉後圖示與換頁不再播放動畫，進度條仍會移動以表示正在處理。"),
    ):
        paragraph = doc.add_paragraph()
        paragraph.paragraph_format.keep_with_next = True
        run = paragraph.add_run(f"問：{question}")
        run.bold = True
        para(doc, f"答：{answer}")


def appendix(doc):
    heading(doc, "附錄 A　清理項目代碼", 1)
    para(doc, "公司設定檔的 disabledJunkCategories 使用下列代碼。瀏覽器快取只在電腦有安裝該瀏覽器時出現。")
    table(doc, ["代碼", "名稱", "預設勾選", "需要系統管理員"], [
        ["user-temp", "使用者暫存檔", "是", "否"],
        ["windows-temp", "Windows 暫存檔", "是", "是"],
        ["edge-cache", "Microsoft Edge 快取", "是", "否"],
        ["chrome-cache", "Google Chrome 快取", "是", "否"],
        ["firefox-cache", "Firefox 快取", "是", "否"],
        ["shader-cache", "顯示卡著色器快取", "否", "否"],
        ["thumbnail-cache", "縮圖快取", "否", "否"],
        ["recent-items", "最近使用的項目", "否", "否"],
        ["crash-dumps", "程式當機傾印檔", "是", "否"],
        ["system-crash-dumps", "系統當機傾印檔", "否", "是"],
        ["user-error-reports", "錯誤報告（使用者）", "是", "否"],
        ["system-error-reports", "錯誤報告（系統）", "是", "是"],
        ["windows-update-cache", "Windows 更新下載快取", "是", "是"],
        ["recycle-bin", "資源回收筒", "否", "否"],
    ], [4.6, 5.4, 2.6, 3.9])
    para(doc, "「一鍵維護」的暫存檔與快取使用上表預設勾選、不需要系統管理員的項目（以系統管理員執行時也包含需要權限的項目），不含資源回收筒；"
              "「使用痕跡」使用 recent-items，停用這個代碼後一鍵維護會顯示「公司設定未開放清除」。")

    heading(doc, "附錄 B　系統調校項目", 1)
    para(doc, "每個開關都是 Windows 本身在「設定」、控制台或群組原則中提供的設定，切換的是相同的登錄值；切換前的值會記在變更紀錄，可以還原。")
    table(doc, ["分類", "項目", "生效時機", "需要系統管理員"], [
        ["效能", "遊戲模式", "立即", "否"],
        ["效能", "硬體加速 GPU 排程", "重新開機後", "是"],
        ["效能", "快速啟動", "下次關機時", "是"],
        ["效能", "透明效果", "立即", "否"],
        ["效能", "儲存空間感知", "立即", "否"],
        ["檔案總管", "顯示副檔名", "重新整理檔案總管視窗後", "否"],
        ["檔案總管", "顯示隱藏的檔案", "重新整理檔案總管視窗後", "否"],
        ["檔案總管", "檔案總管開啟「本機」", "下次開啟檔案總管時", "否"],
        ["檔案總管", "完整的右鍵選單（Windows 11）", "重新啟動檔案總管後", "否"],
        ["檔案總管", "工作列圖示靠左（Windows 11）", "立即", "否"],
        ["檔案總管", "工作列右鍵「結束工作」（Windows 11 23H2 以後）", "立即", "否"],
        ["檔案總管", "開始功能表不搜尋網路", "重新啟動檔案總管後", "是"],
        ["隱私", "關閉廣告識別碼", "立即", "否"],
        ["隱私", "關閉提示與推薦內容", "立即", "否"],
        ["隱私", "關閉量身打造的體驗", "立即", "否"],
        ["隱私", "不記錄最近開啟的檔案", "重新啟動檔案總管後", "否"],
        ["隱私", "關閉活動記錄", "登出或重新開機後", "是"],
        ["隱私", "診斷資料只傳送必要項目", "重新開機後", "是"],
    ], [2.0, 6.2, 5.0, 3.3])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    doc = Document()
    setup(doc)
    cover(doc)
    introduction(doc)
    installation(doc)
    getting_started(doc)
    features(doc)
    command_line(doc)
    faq(doc)
    appendix(doc)
    output = Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    doc.save(output)
    print(f"saved {output} ({figure_number} figures)")


if __name__ == "__main__":
    main()
