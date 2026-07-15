
using System;
using System.Collections.Generic;
using System.Linq;

namespace IVolt.Core.Email.Gmail
{
	////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// Defensive RFC 5322 address parsing that NEVER throws. Replaces any use of
	/// System.Net.Mail.MailAddress (which rejects legal-but-awkward headers — the source of "An
	/// invalid character was found in the mail header: ';'"). Malformed input degrades to raw
	/// capture rather than loss. Returns IVolt's own GmailMailAddress DTO (plain strings, no
	/// validation).
	/// 
	/// Common cases handled:
	///   - "Display Name &lt;user@domain.com&gt;"
	///   - bare "user@domain.com"
	///   - RFC 5322 group syntax:  "Team: a@x.com, b@y.com;"   (semicolons -&gt; flattened)
	///   - commas inside quoted display names or angle brackets (not treated as separators)
	/// Catch-all: anything unrecognized is kept verbatim in Address so nothing is dropped.
	/// </summary>
	///
	/// <remarks>	I Volt, 6/30/2026. </remarks>
	////////////////////////////////////////////////////////////////////////////////////////////////////

	public static class Address_Parsing
	{
		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Parse a single address value. Never throws; returns null only for empty input.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="raw">	The raw. </param>
		///
		/// <returns>	The GmailMailAddress. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static GmailMailAddress SafeAddress(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return null;
			raw = raw.Trim().TrimEnd(';', ',').Trim();

			try
			{
				// "Display Name <user@domain.com>"
				int lt = raw.LastIndexOf('<'), gt = raw.LastIndexOf('>');
				if (lt >= 0 && gt > lt)
				{
					string addr = raw.Substring(lt + 1, gt - lt - 1).Trim();
					string disp = raw.Substring(0, lt).Trim().Trim('"').Trim();
					return new GmailMailAddress
					{
						DisplayName = GmailRfc2047Decode(disp),
						Address = addr
					};
				}

				// Bare address (may still carry a stray comment/param; keep as-is).
				return new GmailMailAddress { DisplayName = "", Address = raw };
			}
			catch
			{
				// Catch-all: preserve the raw value verbatim.
				return new GmailMailAddress { DisplayName = "", Address = raw };
			}
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Parse an address-list header (To/Cc/Bcc/From). Flattens group syntax, splits only on top-
		/// level commas, and never throws.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="header">	The header. </param>
		///
		/// <returns>	A List&lt;GmailMailAddress&gt; </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static List<GmailMailAddress> SafeAddressList(string header)
		{
			var result = new List<GmailMailAddress>();
			if (string.IsNullOrWhiteSpace(header)) return result;

			// Flatten RFC 5322 group syntax: "GroupName: a@x, b@y;" -> "a@x, b@y".
			// Treat a colon as a group prefix ONLY when it precedes the first '@'
			// (otherwise it's part of a mailbox/param, not a group label).
			int at = header.IndexOf('@');
			int colon = header.IndexOf(':');
			if (colon >= 0 && (at < 0 || colon < at))
				header = header.Substring(colon + 1);

			header = header.Replace(';', ','); // any residual group terminators -> separators

			foreach (var token in SplitTopLevel(header, ','))
			{
				var a = SafeAddress(token);
				if (a != null && !string.IsNullOrWhiteSpace(a.Address))
					result.Add(a);
			}
			return result;
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>	First address of a list, or null. </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="header">	The header. </param>
		///
		/// <returns>	The GmailMailAddress. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static GmailMailAddress SafeFirst(string header) =>
			SafeAddressList(header).FirstOrDefault();

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// Split on a delimiter, ignoring delimiters inside "..." quotes or &lt;...&gt; angle brackets,
		/// so commas within display names or addresses don't split incorrectly.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="s">		The string. </param>
		/// <param name="delim">	The delimiter. </param>
		///
		/// <returns>
		/// An enumerator that allows foreach to be used to process split top level in this collection.
		/// </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		public static IEnumerable<string> SplitTopLevel(string s, char delim)
		{
			int start = 0;
			bool inQuote = false, inAngle = false;
			for (int i = 0;i < s.Length;i++)
			{
				char c = s[i];
				if (c == '"' && !inAngle) inQuote = !inQuote;
				else if (c == '<' && !inQuote) inAngle = true;
				else if (c == '>' && !inQuote) inAngle = false;
				else if (c == delim && !inQuote && !inAngle)
				{
					yield return s.Substring(start, i - start);
					start = i + 1;
				}
			}
			if (start <= s.Length - 1 || start == 0)
				yield return s.Substring(start);
		}

		////////////////////////////////////////////////////////////////////////////////////////////////////
		/// <summary>
		/// ---- RFC 2047 hook ------------------------------------------------ If your project already
		/// has GmailRfc2047.Decode, this forwards to it. Kept as a thin wrapper so this file has no hard
		/// compile dependency on it;
		/// replace the body with 'return GmailRfc2047.Decode(s);' if you prefer.
		/// </summary>
		///
		/// <remarks>	I Volt, 7/1/2026. </remarks>
		///
		/// <param name="s">	The string. </param>
		///
		/// <returns>	A string. </returns>
		////////////////////////////////////////////////////////////////////////////////////////////////////

		private static string GmailRfc2047Decode(string s)
		{
			if (string.IsNullOrEmpty(s) || !s.Contains("=?")) return s;
			try { return GmailRfc2047.Decode(s); }   // uses your existing decoder
			catch { return s; }
		}
	}
}
