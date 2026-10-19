"""Rebuild public, synthetic HMI language fixtures using only Python's standard library."""
from pathlib import Path
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED
from xml.etree.ElementTree import Element, SubElement, tostring

ROOT = Path(__file__).resolve().parent.parent / "examples"
S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"


def workbook(name, rows, implicit=False):
    strings = list(dict.fromkeys(value for row in rows for value in row))
    shared = Element("sst", xmlns=S, count=str(sum(map(len, rows))), uniqueCount=str(len(strings)))
    for value in strings:
        # Excel's escape convention must also preserve literal escape-looking strings.
        value = value.replace("_x1234_", "_x005F_x1234_").replace("\r", "_x000D_")
        SubElement(SubElement(shared, "si"), "t").text = value
    sheet = Element("worksheet", xmlns=S)
    if not implicit:
        SubElement(sheet, "dimension", ref=f"A1:{chr(64 + len(rows[0]))}{len(rows)}")
    data = SubElement(sheet, "sheetData")
    for number, values in enumerate(rows, 1):
        row = SubElement(data, "row", **({} if implicit else {"r": str(number)}))
        for col, value in enumerate(values):
            attrs = {"t": "s", "s": "0"} if implicit else {"r": f"{chr(65 + col)}{number}", "t": "s", "s": "0"}
            c = SubElement(row, "c", **attrs)
            SubElement(c, "v").text = str(strings.index(value))
    parts = {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": f'<workbook xmlns="{S}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Strings" sheetId="1" r:id="rId1"/></sheets></workbook>'.encode(),
        "xl/_rels/workbook.xml.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>',
        "xl/styles.xml": f'<styleSheet xmlns="{S}"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf xfId="0"/></cellXfs></styleSheet>'.encode(),
        "xl/sharedStrings.xml": tostring(shared, encoding="utf-8", xml_declaration=True),
        "xl/worksheets/sheet1.xml": tostring(sheet, encoding="utf-8", xml_declaration=True),
    }
    if implicit:
        # Match native v9's legal omitted row/cell addresses and absolute sheet relationship.
        parts["xl/worksheets/sheet.xml"] = parts.pop("xl/worksheets/sheet1.xml")
        parts["[Content_Types].xml"] = parts["[Content_Types].xml"].replace(b"sheet1.xml", b"sheet.xml")
        parts["xl/_rels/workbook.xml.rels"] = parts["xl/_rels/workbook.xml.rels"].replace(b'"worksheets/sheet1.xml"', b'"/xl/worksheets/sheet.xml"')
    with ZipFile(ROOT / name, "w") as archive:
        for path, value in parts.items():
            info = ZipInfo(path, (2020, 1, 1, 0, 0, 0))
            info.compress_type = ZIP_DEFLATED
            archive.writestr(info, value)


workbook("FactoryTalkLanguagesDemo.xls", [
    ["Server", "Component Type", "Component Name", "Description", "en-US", "zh-CN"],
    ["Demo", "Graphic Display", "Overview", "Object 1 Caption", "Ready", "就绪"],
    ["Demo", "Graphic Display", "Overview", "Object 2 Caption", "Start {#1}", "启动 {#1}"],
    ["Demo", "Local Message", "Status", "Message 1", "Line 1\r\nLine 2 _x1234_", "第一行\n第二行"],
])
workbook("ViewDesignerLanguagesDemo.xlsx", [
    ["KEY", "en-US", "es-ES"],
    ["Ready", "Ready", "Listo"],
    ["Start {#1}", "Start {#1}", "Iniciar {#1}"],
])
v9_rows = [[line] for line in ["# Synthetic View Designer localization fixture", "#Source: Generated demo", "#Date: Fixture", "#Version: 1.00", "#", "# Preserve Container, Element, Property and source Key", "#"]] + [
    ["Container", "Element", "Property", "Key:en-US [English]", "zh-Hans [Chinese]", "es-ES [Spanish]"],
    ["Screens/Overview", "Status", "Text", "Ready", "就绪", "Listo"],
    ["Screens/Overview", "StartButton", "Caption", "Ready", "准备", "Preparado"],
    ["Screens/Settings", "OptionalText", "Text", "", "", ""],
    [""],
]
workbook("ViewDesignerV9LanguagesDemo.xlsx", v9_rows, implicit=True)
(ROOT / "ViewDesignerV9LanguagesDemo.txt").write_bytes(b"\xff\xfe" + ("\r\n".join("\t".join(row) for row in v9_rows) + "\r\n").encode("utf-16-le"))
(ROOT / "ViewDesignerLanguagesDemo.txt").write_bytes(b"\xff\xfe" + 'KEY\ten-US\tes-ES\r\nReady\tReady\tListo\r\n"Start {#1}"\t"Start {#1}"\t"Iniciar {#1}"\r\n'.encode("utf-16-le"))
# Synthetic seven-line header. Native headers vary and must be retained verbatim.
header = "\r\n".join(["# Synthetic fixture; not a native application export", "# Header 2", "# Header 3", "# Header 4", "# Header 5", "# Header 6", "# Header 7"]) + "\r\n"
(ROOT / "FactoryTalkLanguagesDemo.txt").write_bytes(b"\xff\xfe" + (header + 'Graphic Display\tOverview\t1\t"Ready"\r\nGraphic Display\tOverview\t2\t"Start {#1}"\r\n').encode("utf-16-le"))
