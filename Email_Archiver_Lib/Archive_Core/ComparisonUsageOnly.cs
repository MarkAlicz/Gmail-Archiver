
using System;
using System.IO;

////////////////////////////////////////////////////////////////////////////////////////////////////
/// <summary>	Process the one. </summary>
///
/// <remarks>	I Volt, 7/1/2026. </remarks>
///
/// <param name="folder"> 	Pathname of the folder. </param>
/// <param name="uid">	  	The UID. </param>
/// <param name="summary">	The summary. </param>
////////////////////////////////////////////////////////////////////////////////////////////////////

private void ProcessOne(IMailFolder folder, UniqueId uid, IMessageSummary summary)
{
	byte[] raw;
	using (var stream = folder.GetStream(uid, string.Empty, 0, int.MaxValue))
	using (var ms = new MemoryStream())
	{
		stream.CopyTo(ms);
		raw = ms.ToArray();
	}

	Gmail_IMAP_Message_Container msg;
	try
	{
		msg = Gmail_IMAP_Message_Processors.Process(raw);
	}
	catch (Exception ex)
	{
		// Catch-all: parsing failed, but we still have the raw octets + hash.
		// Archive a minimal record so the message is never lost.
		_out.WriteLine($"    (header parse degraded for UID {uid}: {ex.Message}; archiving raw)");
		msg = BuildMinimalContainer(raw);
	}

	// Metadata is independent of body parsing — always safe to apply.
	ApplyMetadata(msg, folder, summary);

	_store.StoreMessage(msg);
}

////////////////////////////////////////////////////////////////////////////////////////////////////
/// <summary>
/// Minimal container when full parse fails: raw bytes, hash, and IMAP metadata only.
/// </summary>
///
/// <remarks>	I Volt, 7/1/2026. </remarks>
///
/// <param name="raw">	The raw. </param>
///
/// <returns>	A Gmail_IMAP_Message_Container. </returns>
////////////////////////////////////////////////////////////////////////////////////////////////////

private static Gmail_IMAP_Message_Container BuildMinimalContainer(byte[] raw)
{
	var c = new Gmail_IMAP_Message_Container
	{
		RawBytes = raw,
		RawSha256 = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant(),
	};
	c.ParseWarnings.Add("Full parse failed; archived as raw with metadata only.");
	return c;
}

////////////////////////////////////////////////////////////////////////////////////////////////////
/// <summary>	Applies the metadata. </summary>
///
/// <remarks>	I Volt, 7/1/2026. </remarks>
///
/// <param name="msg">	  	The message. </param>
/// <param name="folder"> 	Pathname of the folder. </param>
/// <param name="summary">	The summary. </param>
////////////////////////////////////////////////////////////////////////////////////////////////////

private void ApplyMetadata(Gmail_IMAP_Message_Container msg, IMailFolder folder, IMessageSummary summary)
{
	msg.Uid = summary.UniqueId.Id;
	msg.UidValidity = folder.UidValidity;
	msg.Rfc822Size = summary.Size ?? 0;
	msg.InternalDate = summary.InternalDate;
	msg.GmMsgId = summary.GMailMessageId ?? 0;
	msg.GmThrId = summary.GMailThreadId ?? 0;

	if (summary.GMailLabels != null)
		foreach (var label in summary.GMailLabels) msg.GmLabels.Add(label);

	if (summary.Flags.HasValue)
	{
		var f = summary.Flags.Value;
		if (f.HasFlag(MessageFlags.Seen)) msg.Flags.Add("\\Seen");
		if (f.HasFlag(MessageFlags.Answered)) msg.Flags.Add("\\Answered");
		if (f.HasFlag(MessageFlags.Flagged)) msg.Flags.Add("\\Flagged");
		if (f.HasFlag(MessageFlags.Deleted)) msg.Flags.Add("\\Deleted");
		if (f.HasFlag(MessageFlags.Draft)) msg.Flags.Add("\\Draft");
	}
}