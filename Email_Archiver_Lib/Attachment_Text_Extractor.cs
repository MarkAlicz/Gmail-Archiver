
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// PdfPig for PDF text extraction (Apache 2.0, fully managed).
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

// Open XML SDK for Word/Excel.
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace IVolt.Core.Email.Gmail
{
    ////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Extracts indexable text from attachment bytes. Supported: plain-text families, PDF, Word
    /// (.docx), Excel (.xlsx). Images and unrecognized binaries return null (indexed by
    /// name/type/size only). Extraction is best-effort and never throws to the caller — failure
    /// yields null.
    /// </summary>
    ///
    /// <remarks>	I Volt, 6/30/2026. </remarks>
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    public static class Attachment_Text_Extractor
    {
        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// (Immutable) Extensions we treat as raw text regardless of declared MIME type.
        /// </summary>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static readonly string[] TextExtensions =
        {
            ".txt", ".csv", ".tsv", ".log", ".md", ".json", ".xml", ".html", ".htm",
            ".yaml", ".yml", ".ini", ".cfg", ".config", ".sql", ".cs", ".c", ".h",
            ".cpp", ".js", ".ts", ".py", ".ps1", ".bat", ".sh", ".eml", ".rtf", ".srt"
        };

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns extracted text, or null if the format is not text-extractable (e.g. images).
        /// </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="fileName">   	Filename of the file. </param>
        /// <param name="contentType">	Type of the content. </param>
        /// <param name="data">		  	The data. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static string Extract(string fileName, string contentType, byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            string ext = (Path.GetExtension(fileName) ?? string.Empty).ToLowerInvariant();
            string ct  = (contentType ?? string.Empty).ToLowerInvariant();

            try
            {
                if (ext == ".pdf" || ct.Contains("pdf"))
                    return ExtractPdf(data);

                if (ext == ".docx" || ct.Contains("wordprocessingml"))
                    return ExtractDocx(data);

                if (ext == ".xlsx" || ct.Contains("spreadsheetml"))
                    return ExtractXlsx(data);

                if (IsTextLike(ext, ct))
                    return DecodeText(data);
            }
            catch
            {
                // Best-effort: any extractor failure means "no indexable content".
                return null;
            }

            return null; // images, zips, unknown binaries -> name/size indexing only
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Query if 'ext' is text like. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="ext">		  	The extent. </param>
        /// <param name="contentType">	Type of the content. </param>
        ///
        /// <returns>	True if text like, false if not. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        public static bool IsTextLike(string ext, string contentType)
        {
            if (TextExtensions.Contains(ext)) return true;
            if (string.IsNullOrEmpty(contentType)) return false;
            return contentType.StartsWith("text/") ||
                   contentType.Contains("json") ||
                   contentType.Contains("xml") ||
                   contentType.Contains("csv");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Decodes a text. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="data">	The data. </param>
        ///
        /// <returns>	A string. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string DecodeText(byte[] data)
        {
            // Honor a UTF-8/UTF-16 BOM if present; otherwise assume UTF-8.
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            return Encoding.UTF8.GetString(data);
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Extracts the PDF described by data. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="data">	The data. </param>
        ///
        /// <returns>	The extracted PDF. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ExtractPdf(byte[] data)
        {
            var sb = new StringBuilder();
            using var ms = new MemoryStream(data);
            using var doc = PdfDocument.Open(ms);
            foreach (Page page in doc.GetPages())
            {
                sb.AppendLine(page.Text);
            }
            return sb.ToString();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Extracts the docx described by data. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="data">	The data. </param>
        ///
        /// <returns>	The extracted docx. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ExtractDocx(byte[] data)
        {
            var sb = new StringBuilder();
            using var ms = new MemoryStream(data);
            using var doc = WordprocessingDocument.Open(ms, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body != null)
            {
                foreach (var text in body.Descendants<W.Text>())
                    sb.Append(text.Text).Append(' ');
            }
            return sb.ToString();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Extracts the XLSX described by data. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="data">	The data. </param>
        ///
        /// <returns>	The extracted XLSX. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ExtractXlsx(byte[] data)
        {
            var sb = new StringBuilder();
            using var ms = new MemoryStream(data);
            using var doc = SpreadsheetDocument.Open(ms, false);
            var wbPart = doc.WorkbookPart;
            if (wbPart == null) return string.Empty;

            // Shared strings table (Excel stores most cell text here).
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;

            foreach (var wsPart in wbPart.WorksheetParts)
            {
                foreach (var cell in wsPart.Worksheet.Descendants<X.Cell>())
                {
                    string val = ReadCell(cell, sst);
                    if (!string.IsNullOrEmpty(val)) sb.Append(val).Append(' ');
                }
            }
            return sb.ToString();
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////
        /// <summary>	Reads a cell. </summary>
        ///
        /// <remarks>	I Volt, 7/1/2026. </remarks>
        ///
        /// <param name="cell">	The cell. </param>
        /// <param name="sst"> 	The sst. </param>
        ///
        /// <returns>	The cell. </returns>
        ////////////////////////////////////////////////////////////////////////////////////////////////////

        private static string ReadCell(X.Cell cell, X.SharedStringTable sst)
        {
            if (cell?.CellValue == null) return null;
            string raw = cell.CellValue.InnerText;
            if (cell.DataType != null && cell.DataType.Value == X.CellValues.SharedString)
            {
                if (sst != null && int.TryParse(raw, out int idx) && idx >= 0 && idx < sst.ChildElements.Count)
                    return sst.ChildElements[idx].InnerText;
                return null;
            }
            return raw;
        }
    }
}
