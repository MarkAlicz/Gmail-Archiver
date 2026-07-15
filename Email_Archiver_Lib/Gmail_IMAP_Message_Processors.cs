

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

using Lucene.Net.Util;



namespace IVolt.Core.Email.Gmail
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>	A gmail IMAP message processors. </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public static class Gmail_IMAP_Message_Processors
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Entry points
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <exception cref="ArgumentNullException">	Thrown when one or more required arguments are
		/// 											null. </exception>
		///
		/// <param name="raw">	The raw. </param>
		///
		/// <returns>	A Gmail_IMAP_Message_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Gmail_IMAP_Message_Container Process(byte[] raw)
		{
			if (raw is null) throw new ArgumentNullException(nameof(raw));
			var c = new Gmail_IMAP_Message_Container { RawBytes = raw };
			ProcessInto(c, raw);
			return c;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Process this object. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <exception cref="ArgumentNullException">	Thrown when one or more required arguments are
		/// 											null. </exception>
		///
		/// <param name="raw">			 	The raw. </param>
		/// <param name="sourceEncoding">	(Optional) Source encoding. </param>
		///
		/// <returns>	A Gmail_IMAP_Message_Container. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static Gmail_IMAP_Message_Container Process(string raw, Encoding? sourceEncoding = null)
		{
			if (raw is null) throw new ArgumentNullException(nameof(raw));
			Encoding enc = sourceEncoding ?? Encoding.UTF8;
			var c = new Gmail_IMAP_Message_Container
			{
				ParsedFromString = true,
				SourceEncoding = enc.WebName
			};
			c.ParseWarnings.Add(
				$"Parsed from string under '{enc.WebName}'. RawSha256 reflects re-encoded " +
				"bytes, not the original wire octets. Prefer Process(byte[]) for provenance.");
			ProcessInto(c, enc.GetBytes(raw));
			return c;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Process the into. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">  	A Gmail_IMAP_Message_Container to process. </param>
		/// <param name="raw">	The raw. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void ProcessInto(Gmail_IMAP_Message_Container c, byte[] raw)
		{
			c.RawBytes = raw;
			c.RawSha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
			c.HasBareLf = DetectBareLf(raw);

			int split = FindHeaderBodySplit(raw, out int bodyStart, out _);
			if (split < 0)
			{
				c.ParseWarnings.Add("No header/body separator found; treating input as headers.");
				split = raw.Length;
				bodyStart = raw.Length;
			}

			string headerBlock = Encoding.Latin1.GetString(raw, 0, split);
			c.Had8BitHeaders = raw.Take(split).Any(b => b > 0x7F);
			if (c.Had8BitHeaders)
				c.ParseWarnings.Add("Raw 8-bit octets in header block (should be RFC 2047 encoded).");

			ParseHeaders(c, headerBlock);
			PopulateWellKnownHeaders(c);

			byte[] bodyBytes = raw[bodyStart..];
			ParseBody(c, bodyBytes);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Header parsing
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">		  	A Gmail_IMAP_Message_Container to process. </param>
		/// <param name="headerBlock">	The header block. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static void ParseHeaders(Gmail_IMAP_Message_Container c, string headerBlock)
		{
			var logicalLines = new List<string>();
			foreach (var line in headerBlock.Split('\n'))
			{
				string l = line.TrimEnd('\r');
				if (l.Length == 0) continue;
				if ((l[0] == ' ' || l[0] == '\t') && logicalLines.Count > 0)
					logicalLines[^1] += " " + l.TrimStart();
				else
					logicalLines.Add(l);
			}

			foreach (var l in logicalLines)
			{
				int colon = l.IndexOf(':');
				if (colon <= 0)
				{
					c.ParseWarnings.Add($"Malformed header line (no colon): '{Trunc(l)}'");
					continue;
				}
				string name = l[..colon].Trim();
				string value = l[(colon + 1)..].Trim();
				c.Headers.Add(new(name, value));
				if (!c.HeaderIndex.TryGetValue(name, out var list))
					c.HeaderIndex[name] = list = new List<string>();
				list.Add(value);
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Populates a well known headers. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">	A Gmail_IMAP_Message_Container to process. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void PopulateWellKnownHeaders(Gmail_IMAP_Message_Container c)
		{
			c.MessageId = Unbracket(c.GetHeader("Message-ID") ?? string.Empty);
			c.Subject = GmailRfc2047.Decode(c.GetHeader("Subject") ?? string.Empty);
			c.InReplyTo = Unbracket(c.GetHeader("In-Reply-To") ?? string.Empty);
			c.MimeVersion = c.GetHeader("MIME-Version") ?? string.Empty;
			c.From = Address_Parsing.SafeFirst(c.GetHeader("From"));
			c.To.AddRange(Address_Parsing.SafeAddressList(c.GetHeader("To")));
			c.Cc.AddRange(Address_Parsing.SafeAddressList(c.GetHeader("Cc")));
			c.Bcc.AddRange(Address_Parsing.SafeAddressList(c.GetHeader("Bcc")));
			// Sender and Reply-To are distinct semantics — route them to their own
			// container slots rather than polluting the Bcc recipient list.
			c.Sender = Address_Parsing.SafeFirst(c.GetHeader("Sender"));
			c.ReplyTo = Address_Parsing.SafeFirst(c.GetHeader("Reply-To"));


			foreach (var r in (c.GetHeader("References") ?? string.Empty)
					 .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
				c.References.Add(Unbracket(r));

			if (TryParseDate(c.GetHeader("Date"), out var d)) c.Date = d;

			string ct = c.GetHeader("Content-Type") ?? string.Empty;
			c.ContentType = ct.Split(';')[0].Trim();
			c.Charset = ExtractParam(ct, "charset");
			c.Boundary = ExtractParam(ct, "boundary");
			c.ContentTransferEncoding = (c.GetHeader("Content-Transfer-Encoding") ?? string.Empty)
				.Trim().ToLowerInvariant();
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Body / MIME parsing
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">   	A Gmail_IMAP_Message_Container to process. </param>
		/// <param name="body">	The body. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static void ParseBody(Gmail_IMAP_Message_Container c, byte[] body)
		{
			if (!string.IsNullOrEmpty(c.Boundary) &&
				c.ContentType.StartsWith("multipart", StringComparison.OrdinalIgnoreCase))
			{
				ParseMultipart(c, body, c.Boundary);
			}
			else
			{
				var part = DecodeSinglePart(body, c.ContentType, c.Charset,
											c.ContentTransferEncoding, c.Headers);
				c.Parts.Add(part);
				AssignBodyRoles(c, part);
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Parse multipart. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">	   	A Gmail_IMAP_Message_Container to process. </param>
		/// <param name="body">	   	The body. </param>
		/// <param name="boundary">	The boundary. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void ParseMultipart(Gmail_IMAP_Message_Container c, byte[] body, string boundary)
		{
			byte[] delim = Encoding.ASCII.GetBytes("--" + boundary);
			var segments = SplitOnBoundary(body, delim);
			foreach (var seg in segments)
			{
				int hb = FindHeaderBodySplit(seg, out int bs, out _);
				if (hb < 0) continue;

				// Parse the sub-part's headers into a throwaway container.
				string subHdr = Encoding.Latin1.GetString(seg, 0, hb);
				var sub = new Gmail_IMAP_Message_Container();
				ParseHeaders(sub, subHdr);

				string sct = sub.GetHeader("Content-Type") ?? "text/plain";
				string scharset = ExtractParam(sct, "charset");
				string scte = (sub.GetHeader("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant();
				string sBoundary = ExtractParam(sct, "boundary");

				byte[] segBody = seg[bs..];
				if (!string.IsNullOrEmpty(sBoundary) &&
					sct.StartsWith("multipart", StringComparison.OrdinalIgnoreCase))
				{
					ParseMultipart(c, segBody, sBoundary);   // nested — parts flow into c
					continue;
				}
				var part = DecodeSinglePart(segBody, sct.Split(';')[0].Trim(),
											scharset, scte, sub.Headers);
				c.Parts.Add(part);
				AssignBodyRoles(c, part);
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Assign body roles. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="c">   	A Gmail_IMAP_Message_Container to process. </param>
		/// <param name="part">	The part. </param>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static void AssignBodyRoles(Gmail_IMAP_Message_Container c, GmailMimePart part)
		{
			if (part.IsAttachment) return;
			if (string.IsNullOrEmpty(c.TextBody) &&
				part.ContentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
				c.TextBody = part.TextContent;
			else if (string.IsNullOrEmpty(c.HtmlBody) &&
					 part.ContentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
				c.HtmlBody = part.TextContent;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Decodes a single part. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="raw">		  	The raw. </param>
		/// <param name="contentType">	Type of the content. </param>
		/// <param name="charset">	  	The charset. </param>
		/// <param name="cte">		  	The cte. </param>
		/// <param name="headers">	  	The headers. </param>
		///
		/// <returns>	A GmailMimePart. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static GmailMimePart DecodeSinglePart(
			byte[] raw, string contentType, string charset, string cte,
			List<KeyValuePair<string, string>> headers)
		{
			byte[] decoded = cte switch
			{
				"base64" => SafeFromBase64(raw),
				"quoted-printable" => GmailQuotedPrintable.Decode(raw),
				_ => raw
			};

			var disp = headers.FirstOrDefault(h =>
				h.Key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)).Value ?? "";
			bool isAttachment = disp.StartsWith("attachment", StringComparison.OrdinalIgnoreCase)
								|| !string.IsNullOrEmpty(ExtractParam(disp, "filename"));
			string filename = GmailRfc2047.Decode(ExtractParam(disp, "filename"));
			if (string.IsNullOrEmpty(filename))
				filename = GmailRfc2047.Decode(ExtractParam(contentType, "name"));

			var part = new GmailMimePart
			{
				ContentType = contentType,
				Charset = charset,
				TransferEncoding = cte,
				RawContent = decoded,
				IsAttachment = isAttachment,
				FileName = filename,
				ContentId = Unbracket(headers.FirstOrDefault(h =>
					h.Key.Equals("Content-ID", StringComparison.OrdinalIgnoreCase)).Value ?? "")
			};

			if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !isAttachment)
				part.TextContent = ResolveEncoding(charset).GetString(decoded);

			return part;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ===================================================================== Helpers (unchanged
		/// logic, now fully static/self-contained)
		/// =====================================================================.
		/// </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="data">			The data. </param>
		/// <param name="bodyStart">	[out] The body start. </param>
		/// <param name="crlf">			[out] True to newline. </param>
		///
		/// <returns>	The found header body split. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static int FindHeaderBodySplit(byte[] data, out int bodyStart, out bool crlf)
		{
			for (int i = 0;i < data.Length - 1;i++)
			{
				if (data[i] == (byte)'\n' && data[i + 1] == (byte)'\n')
				{ bodyStart = i + 2; crlf = false; return i; }
				if (i < data.Length - 3 &&
					data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
					data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
				{ bodyStart = i + 4; crlf = true; return i + 2; }
			}
			bodyStart = -1; crlf = true; return -1;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Detect bare line feed. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="data">	The data. </param>
		///
		/// <returns>	True if it succeeds, false if it fails. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static bool DetectBareLf(byte[] data)
		{
			for (int i = 0;i < data.Length;i++)
				if (data[i] == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r'))
					return true;
			return false;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Splits on boundary. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="body"> 	The body. </param>
		/// <param name="delim">	The delimiter. </param>
		///
		/// <returns>	A List&lt;byte[]&gt; </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static List<byte[]> SplitOnBoundary(byte[] body, byte[] delim)
		{
			var result = new List<byte[]>();
			var positions = new List<int>();
			for (int i = 0;i <= body.Length - delim.Length;i++)
			{
				bool match = true;
				for (int j = 0;j < delim.Length;j++)
					if (body[i + j] != delim[j]) { match = false; break; }
				if (match) positions.Add(i);
			}
			for (int p = 0;p < positions.Count - 1;p++)
			{
				int start = positions[p] + delim.Length;
				while (start < body.Length && (body[start] == (byte)'\r' || body[start] == (byte)'\n')) start++;
				int end = positions[p + 1];
				if (end > start) result.Add(body[start..end]);
			}
			return result;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Safe from base 64. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="raw">	The raw. </param>
		///
		/// <returns>	A byte[]. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static byte[] SafeFromBase64(byte[] raw)
		{
			try
			{
				string s = Encoding.ASCII.GetString(raw);
				s = new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
				return Convert.FromBase64String(s);
			}
			catch { return raw; }
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Resolve encoding. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="charset">	The charset. </param>
		///
		/// <returns>	An Encoding. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static Encoding ResolveEncoding(string charset)
		{
			if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
			try { return Encoding.GetEncoding(charset); }
			catch { return Encoding.UTF8; }
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Extracts the parameter. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="headerValue">	The header value. </param>
		/// <param name="param">	  	The parameter. </param>
		///
		/// <returns>	The extracted parameter. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string ExtractParam(string headerValue, string param)
		{
			foreach (var seg in headerValue.Split(';').Skip(1))
			{
				var kv = seg.Split(new[] { '=' }, 2);
				if (kv.Length == 2 && kv[0].Trim().Equals(param, StringComparison.OrdinalIgnoreCase))
					return kv[1].Trim().Trim('"');
			}
			return string.Empty;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Unbrackets. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="s">	The string. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Unbracket(string s) => s.Trim().Trim('<', '>').Trim();

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Truncs. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="s">	The string. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string Trunc(string s) => s.Length > 80 ? s[..80] + "…" : s;

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Parse first address. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="header">	The header. </param>
		///
		/// <returns>	A MailAddress? </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static MailAddress? ParseFirstAddress(string? header) =>
			string.IsNullOrWhiteSpace(header) ? null : ParseAddressList(header).FirstOrDefault();

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Parse address list. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="header">	The header. </param>
		///
		/// <returns>	A List&lt;MailAddress&gt; </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static List<MailAddress> ParseAddressList(string? header)
		{
			var result = new List<MailAddress>();
			if (string.IsNullOrWhiteSpace(header)) return result;
			foreach (var token in header.Split(','))
			{
				string t = token.Trim();
				if (t.Length == 0) continue;
				string display = "", addr = t;
				int lt = t.LastIndexOf('<'), gt = t.LastIndexOf('>');
				if (lt >= 0 && gt > lt)
				{
					addr = t[(lt + 1)..gt].Trim();
					display = GmailRfc2047.Decode(t[..lt].Trim().Trim('"'));
				}
				result.Add(new MailAddress(addr, display));
			}
			return result;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	Attempts to parse date a DateTimeOffset from the given string. </summary>
		///
		/// <remarks>	I Volt, 6/30/2026. </remarks>
		///
		/// <param name="s">  	The string. </param>
		/// <param name="dto">	[out] The data transfer object. </param>
		///
		/// <returns>	True if it succeeds, false if it fails. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static bool TryParseDate(string? s, out DateTimeOffset dto)
		{
			dto = default;
			if (string.IsNullOrWhiteSpace(s)) return false;
			return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
				DateTimeStyles.AllowWhiteSpaces, out dto);
		}
	}
}