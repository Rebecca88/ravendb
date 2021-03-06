//-----------------------------------------------------------------------
// <copyright file="Attachments.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;
using Raven.Abstractions.Logging;
using Raven.Database.Impl;
using Raven.Imports.Newtonsoft.Json;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Storage;
using Raven.Json.Linq;

namespace Raven.Storage.Esent.StorageActions
{
	public partial class DocumentStorageActions : IAttachmentsStorageActions
	{
		public Guid AddAttachment(string key, Guid? etag, Stream data, RavenJObject headers)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			var isUpdate = Api.TrySeek(session, Files, SeekGrbit.SeekEQ);
			if (isUpdate)
			{
				var existingEtag = Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["etag"]).TransfromToGuidWithProperSorting();
				if (existingEtag != etag && etag != null)
				{
					throw new ConcurrencyException("PUT attempted on attachment '" + key +
						"' using a non current etag")
					{
						ActualETag = existingEtag,
						ExpectedETag = etag.Value
					};
				}
			}
			else
			{
				if (data == null)
					throw new InvalidOperationException("When adding new attachment, the attachment data must be specified");

				if (Api.TryMoveFirst(session, Details))
					Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["attachment_count"], 1);
			}

			Guid newETag = uuidGenerator.CreateSequentialUuid(UuidType.Attachments);
			using (var update = new Update(session, Files, isUpdate ? JET_prep.Replace : JET_prep.Insert))
			{
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["name"], key, Encoding.Unicode);
				if (data != null)
				{
					long written;
					using (var columnStream = new ColumnStream(session, Files, tableColumnsCache.FilesColumns["data"]))
					{
						if (isUpdate)
							columnStream.SetLength(0);
						using (var stream = new BufferedStream(columnStream))
						{
							data.CopyTo(stream);
							written = stream.Position;
							stream.Flush();
						}
					}
					if (written == 0) // empty attachment
					{
						Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["data"], new byte[0]);
					}
				}
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["etag"], newETag.TransformToValueForEsentSorting());
				Api.SetColumn(session, Files, tableColumnsCache.FilesColumns["metadata"], headers.ToString(Formatting.None), Encoding.Unicode);

				update.Save();
			}
			logger.Debug("Adding attachment {0}", key);

			return newETag;
		}

		public void DeleteAttachment(string key, Guid? etag)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
			{
				logger.Debug("Attachment with key '{0}' was not found, and considered deleted", key);
				return;
			}

			var fileEtag = Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["etag"]).TransfromToGuidWithProperSorting();
			if (fileEtag != etag && etag != null)
			{
				throw new ConcurrencyException("DELETE attempted on attachment '" + key +
					"' using a non current etag")
				{
					ActualETag = fileEtag,
					ExpectedETag = etag.Value
				};
			}

			Api.JetDelete(session, Files);

			if (Api.TryMoveFirst(session, Details))
				Api.EscrowUpdate(session, Details, tableColumnsCache.DetailsColumns["attachment_count"], -1);
			logger.Debug("Attachment with key '{0}' was deleted", key);
		}

		public IEnumerable<AttachmentInformation> GetAttachmentsByReverseUpdateOrder(int start)
		{
			Api.JetSetCurrentIndex(session, Files, "by_etag");
			Api.MoveAfterLast(session, Files);
			for (int i = 0; i < start; i++)
			{
				if (Api.TryMovePrevious(session, Files) == false)
					yield break;
			}
			while (Api.TryMovePrevious(session, Files))
			{
				yield return ReadCurrentAttachmentInformation();
			}
		}

		public IEnumerable<AttachmentInformation> GetAttachmentsStartingWith(string idPrefix, int start, int pageSize)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekGE) == false)
				return Enumerable.Empty<AttachmentInformation>();

			var optimizer = new OptimizedIndexReader(pageSize);
			do
			{
				Api.MakeKey(session, Files, idPrefix, Encoding.Unicode, MakeKeyGrbit.NewKey | MakeKeyGrbit.SubStrLimit);
				if (Api.TrySetIndexRange(session, Files, SetIndexRangeGrbit.RangeUpperLimit | SetIndexRangeGrbit.RangeInclusive) == false)
					return Enumerable.Empty<AttachmentInformation>();

				while (start > 0)
				{
					if (Api.TryMoveNext(session, Files) == false)
						return Enumerable.Empty<AttachmentInformation>();
					start--;
				}

				optimizer.Add(Session, Files);

			} while (Api.TryMoveNext(session, Files) && optimizer.Count < pageSize);

			return optimizer.Select(Session, Files, ReadCurrentAttachmentInformation);

		}

		public IEnumerable<AttachmentInformation> GetAttachmentsAfter(Guid etag, int take, long maxTotalSize)
		{
			Api.JetSetCurrentIndex(session, Files, "by_etag");
			Api.MakeKey(session, Files, etag.TransformToValueForEsentSorting(), MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekGT) == false)
				return Enumerable.Empty<AttachmentInformation>();

			var optimizer = new OptimizedIndexReader(take);
			do
			{
				optimizer.Add(Session, Files);
			} while (Api.TryMoveNext(session, Files) && optimizer.Count < take);

			long totalSize = 0;

			return optimizer
				.Where(_ => totalSize <= maxTotalSize)
				.Select(Session, Files, o => ReadCurrentAttachmentInformation())
				.Select(x =>
				{
					totalSize += x.Size;
					return x;
				});
		}

		private AttachmentInformation ReadCurrentAttachmentInformation()
		{
			return new AttachmentInformation
			{
				Size = Api.RetrieveColumnSize(session, Files, tableColumnsCache.FilesColumns["data"]) ?? 0,
				Etag = Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["etag"]).TransfromToGuidWithProperSorting(),
				Key = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["name"], Encoding.Unicode),
				Metadata =
					RavenJObject.Parse(Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["metadata"],
																  Encoding.Unicode))
			};
		}

		public Attachment GetAttachment(string key)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
			{
				return null;
			}

			var metadata = Api.RetrieveColumnAsString(session, Files, tableColumnsCache.FilesColumns["metadata"], Encoding.Unicode);
			return new Attachment
			{
				Key = key,
				Data = () =>
				{
					StorageActionsAccessor storageActionsAccessor = transactionalStorage.GetCurrentBatch();
					var documentStorageActions = ((DocumentStorageActions)storageActionsAccessor.Attachments);
					return documentStorageActions.GetAttachmentStream(key);
				},
				Size = (int)GetAttachmentStream(key).Length,
				Etag = Api.RetrieveColumn(session, Files, tableColumnsCache.FilesColumns["etag"]).TransfromToGuidWithProperSorting(),
				Metadata = RavenJObject.Parse(metadata)
			};
		}


		private Stream GetAttachmentStream(string key)
		{
			Api.JetSetCurrentIndex(session, Files, "by_name");
			Api.MakeKey(session, Files, key, Encoding.Unicode, MakeKeyGrbit.NewKey);
			if (Api.TrySeek(session, Files, SeekGrbit.SeekEQ) == false)
			{
				throw new InvalidOperationException("Could not find attachment named: " + key);
			}

			return new BufferedStream(new ColumnStream(session, Files, tableColumnsCache.FilesColumns["data"]));
		}
	}
}
