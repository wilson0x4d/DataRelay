﻿using System;
using System.Text;
using System.Collections.Generic;
using MySpace.Common.IO;
using MySpace.DataRelay.Interfaces.Query.IndexCacheV3;

namespace MySpace.DataRelay.Common.Interfaces.Query.IndexCacheV3
{
    public class SpanQuery : BaseMultiIndexIdQuery<SpanQueryResult>
    {
        #region Data Members

        /// <summary>
        /// One-based start position
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Number of items to get beginning at the offset. Set Span to zero to get all items
        /// </summary>
        public int Span { get; set; }

        /// <summary>
        /// Set this to true to get total number of items that satisfy Filter(s)
        /// </summary>
        public new bool GetAdditionalAvailableItemCount
        {
            get
            {
                return base.GetAdditionalAvailableItemCount;
            }
            set
            {
                base.GetAdditionalAvailableItemCount = value;
            }
        }

        internal override int MaxMergeCount
        {
            get
            {
                return (Span == 0) ? Int32.MaxValue : Offset - 1 + Span;
            }
        }

        #endregion

        #region Ctors

        public SpanQuery()
        {
            Init(-1, -1);
        }

        public SpanQuery(SpanQuery query)
            : base(query)
        {
            Init(query.Offset, query.Span);
        }

        private void Init(int offset, int span)
        {
            Offset = offset;
            Span = span;
        }

        #endregion

        #region Methods

        public override string ToString()
        {
            var stb = new StringBuilder();
            stb.Append("--- Span Query ---");
            stb.Append("(").Append(" Offset: ").Append(Offset).Append("),");
            stb.Append("(").Append("Span: ").Append(Span).Append("),");
            stb.Append(base.ToString());
            return stb.ToString();
        }

        #endregion

        #region IMergeableQueryResult<TQueryResult> Members

        public override SpanQueryResult MergeResults(IList<SpanQueryResult> partialResults)
        {
            SpanQueryResult finalResult = default(SpanQueryResult);

            if (partialResults == null || partialResults.Count == 0)
            {
                return finalResult;
            }

            // We have partialResults to process
            ByteArrayEqualityComparer byteArrayEqualityComparer = new ByteArrayEqualityComparer();
            BaseComparer baseComparer = null;
            Dictionary<byte[] /*IndexId*/, IndexHeader /*IndexHeader*/> completeIndexIdIndexHeaderMapping =
                new Dictionary<byte[], IndexHeader>(byteArrayEqualityComparer);

            if (partialResults.Count == 1)
            {
                #region  Just one cluster was targeted, so no need to merge anything

                finalResult = partialResults[0];

                #endregion
            }
            else
            {
                #region  More than one clusters was targeted

                List<ResultItem> completeResultItemList = new List<ResultItem>();
                GroupByResult completeGroupByResult = new GroupByResult(null);
                int totalCount = 0;
                int additionalAvailableItemCount = 0;
                StringBuilder exceptionStringBuilder = new StringBuilder();
                bool indexCapSet = false;
                int indexCap = 0;

                foreach (SpanQueryResult partialResult in partialResults)
                {
                    if (partialResult != null)
                    {
                        #region Compute TotalCount

                        totalCount += partialResult.TotalCount;

                        #endregion

                        #region Assign IndexCap

                        if (!indexCapSet)
                        {
                            indexCap = partialResult.IndexCap;
                            indexCapSet = true;
                        }

                        #endregion

                        #region Compute PageableItemCount

                        if (GetAdditionalAvailableItemCount)
                        {
                            additionalAvailableItemCount += partialResult.AdditionalAvailableItemCount;
                        }

                        #endregion

                        #region Merge Results

                        if ((partialResult.ResultItemList != null && partialResult.ResultItemList.Count > 0) ||
                            (partialResult.GroupByResult != null && partialResult.GroupByResult.Count > 0))
                        {
                            if (baseComparer == null)
                            {
                                baseComparer = new BaseComparer(partialResult.IsTagPrimarySort,
                                                                partialResult.SortFieldName, 
                                                                partialResult.SortOrderList);
                                completeGroupByResult = new GroupByResult(baseComparer);
                            }

                            if (GroupBy == null)
                            {
                                MergeAlgo.MergeItemLists(ref completeResultItemList,
                                                         partialResult.ResultItemList,
                                                         MaxMergeCount,
                                                         baseComparer);
                            }
                            else
                            {
                                MergeAlgo.MergeGroupResult(ref completeGroupByResult,
                                                           partialResult.GroupByResult,
                                                           MaxMergeCount,
                                                           baseComparer);
                            }
                        }

                        #endregion

                        #region Update IndexIdIndexHeaderMapping

                        if (GetIndexHeaderType != GetIndexHeaderType.None && 
                            partialResult.IndexIdIndexHeaderMapping != null && 
                            partialResult.IndexIdIndexHeaderMapping.Count > 0)
                        {
                            foreach (KeyValuePair<byte[], IndexHeader> kvp in partialResult.IndexIdIndexHeaderMapping)
                            {
                                if (!completeIndexIdIndexHeaderMapping.ContainsKey(kvp.Key))
                                {
                                    completeIndexIdIndexHeaderMapping.Add(kvp.Key, kvp.Value);
                                }
                            }
                        }

                        #endregion

                        #region Update exceptionInfo

                        if (!String.IsNullOrEmpty(partialResult.ExceptionInfo))
                        {
                            exceptionStringBuilder.Append(partialResult.ExceptionInfo);
                            exceptionStringBuilder.Append(" ");
                        }

                        #endregion
                    }
                }

                #region Create FinalResult

                finalResult = new SpanQueryResult
                {
                    ResultItemList = completeResultItemList,
                    GroupByResult = completeGroupByResult,
                    TotalCount = totalCount,
                    AdditionalAvailableItemCount = additionalAvailableItemCount,
                    IndexCap = indexCap
                };

                //Assign sort fields for use in GroupBy remote queries
                if (baseComparer != null)
                {
                    finalResult.IsTagPrimarySort = baseComparer.IsTagPrimarySort;
                    finalResult.SortFieldName = baseComparer.SortFieldName;
                    finalResult.SortOrderList = baseComparer.SortOrderList;
                }

                if (GetIndexHeaderType != GetIndexHeaderType.None && completeIndexIdIndexHeaderMapping.Count > 0)
                {
                    finalResult.IndexIdIndexHeaderMapping = completeIndexIdIndexHeaderMapping;
                }

                if (exceptionStringBuilder.Length > 0)
                {
                    finalResult.ExceptionInfo = exceptionStringBuilder.ToString();
                }

                #endregion

                #endregion
            }

            #region Spanning and Update IndexIdIndexHeaderMapping if required

            if (ClientSideSubsetProcessingRequired && Span != 0 && finalResult != null)
            {
                #region Spanning

                if (GroupBy == null)
                {
                    List<ResultItem> filteredResultItemList = new List<ResultItem>();
                    if (finalResult.ResultItemList.Count >= Offset)
                    {
                        for (int i = Offset - 1; i < finalResult.ResultItemList.Count && filteredResultItemList.Count < Span; i++)
                        {
                            filteredResultItemList.Add(finalResult.ResultItemList[i]);
                        }
                    }
                    finalResult.ResultItemList = filteredResultItemList;
                }
                else if (finalResult.GroupByResult != null && finalResult.GroupByResult.Count > 0)
                {
                    GroupByResult filteredGroupByResult = new GroupByResult(baseComparer);
                    if (finalResult.GroupByResult.Count >= Offset)
                    {
                        for (int i = Offset - 1; i < finalResult.GroupByResult.Count && filteredGroupByResult.Count < Span; i++)
                        {
                            filteredGroupByResult.Add(finalResult.GroupByResult[i].CompositeKey, finalResult.GroupByResult[i]);
                        }
                    }
                    finalResult.GroupByResult = filteredGroupByResult;
                }

                #endregion

                #region Update IndexIdIndexHeaderMapping to only include metadata relevant after paging

                if (partialResults.Count != 1 && GetIndexHeaderType == GetIndexHeaderType.ResultItemsIndexIds && completeIndexIdIndexHeaderMapping.Count > 0)
                {
                    Dictionary<byte[] /*IndexId*/, IndexHeader /*IndexHeader*/> filteredIndexIdIndexHeaderMapping = new Dictionary<byte[], IndexHeader>(byteArrayEqualityComparer);
                    if (GroupBy == null)
                    {
                        foreach (ResultItem resultItem in finalResult.ResultItemList)
                        {
                            if (!filteredIndexIdIndexHeaderMapping.ContainsKey(resultItem.IndexId))
                            {
                                filteredIndexIdIndexHeaderMapping.Add(resultItem.IndexId, completeIndexIdIndexHeaderMapping[resultItem.IndexId]);
                            }
                        }
                    }
                    else if (finalResult.GroupByResult != null && finalResult.GroupByResult.Count > 0)
                    {
                        foreach (ResultItemBag resultItemBag in finalResult.GroupByResult)
                        {
                            for (int i = 0; i < resultItemBag.Count; i++)
                            {
                                ResultItem resultItem = resultItemBag[i];
                                if (!filteredIndexIdIndexHeaderMapping.ContainsKey(resultItem.IndexId))
                                {
                                    filteredIndexIdIndexHeaderMapping.Add(resultItem.IndexId, completeIndexIdIndexHeaderMapping[resultItem.IndexId]);
                                }
                            }
                        }
                    }
                    finalResult.IndexIdIndexHeaderMapping = filteredIndexIdIndexHeaderMapping.Count > 0 ? filteredIndexIdIndexHeaderMapping : null;
                }

                #endregion
            }

            #endregion
            return finalResult;
        }

        #endregion

        #region IRelayMessageQuery Members

        public override byte QueryId
        {
            get
            {
                return (byte)QueryTypes.SpanQuery;
            }
        }

        #endregion

        #region IVersionSerializable Members

        public override void Serialize(IPrimitiveWriter writer)
        {
            //Offset
            writer.Write(Offset);

            //Span
            writer.Write(Span);

            //TargetIndexName
            writer.Write(TargetIndexName);

            //TagsFromIndexes
            if (TagsFromIndexes == null || TagsFromIndexes.Count == 0)
            {
                writer.Write((ushort)0);
            }
            else
            {
                writer.Write((ushort)TagsFromIndexes.Count);
                foreach (string str in TagsFromIndexes)
                {
                    writer.Write(str);
                }
            }

            //TagSort
            if (TagSort == null)
            {
                writer.Write((byte)0);
            }
            else
            {
                writer.Write((byte)1);
                Serializer.Serialize(writer.BaseStream, TagSort);
            }

            //IndexIdList
            if (IndexIdList == null || IndexIdList.Count == 0)
            {
                writer.Write((ushort)0);
            }
            else
            {
                writer.Write((ushort)IndexIdList.Count);
                foreach (byte[] indexId in IndexIdList)
                {
                    if (indexId == null || indexId.Length == 0)
                    {
                        writer.Write((ushort)0);
                    }
                    else
                    {
                        writer.Write((ushort)indexId.Length);
                        writer.Write(indexId);
                    }
                }
            }

            //Write a byte to account for deprecated CriterionList
            writer.Write((byte)0);

            //MaxItemsPerIndex
            writer.Write(MaxItems);

            //ExcludeData
            writer.Write(ExcludeData);

            //GetIndexHeader
            writer.Write(GetIndexHeader);


            //GetAdditionalAvailableItemCount
            writer.Write(GetAdditionalAvailableItemCount);

            //PrimaryIdList
            if (PrimaryIdList == null || PrimaryIdList.Count == 0)
            {
                writer.Write((ushort)0);
            }
            else
            {
                writer.Write((ushort)PrimaryIdList.Count);
                foreach (int primaryId in PrimaryIdList)
                {
                    writer.Write(primaryId);
                }
            }

            //Filter
            if (Filter == null)
            {
                writer.Write((byte)0);
            }
            else
            {
                writer.Write((byte)Filter.FilterType);
                Serializer.Serialize(writer.BaseStream, Filter);
            }

            //IndexIdParamsMapping
            if (IndexIdParamsMapping == null || IndexIdParamsMapping.Count == 0)
            {
                writer.Write((ushort)0);
            }
            else
            {
                writer.Write((ushort)IndexIdParamsMapping.Count);
                foreach (KeyValuePair<byte[] /*IndexId*/, IndexIdParams /*IndexIdParams*/> kvp in IndexIdParamsMapping)
                {
                    //IndexId
                    if (kvp.Key == null || kvp.Key.Length == 0)
                    {
                        writer.Write((ushort)0);

                        //No need to serialize IndexIdParams
                    }
                    else
                    {
                        writer.Write((ushort)kvp.Key.Length);
                        writer.Write(kvp.Key);

                        //IndexIdParams
                        Serializer.Serialize(writer, kvp.Value);
                    }
                }
            }

            //FullDataIdInfo
            if (FullDataIdInfo == null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                Serializer.Serialize(writer.BaseStream, FullDataIdInfo);
            }

            //ClientSideSubsetProcessingRequired
            writer.Write(ClientSideSubsetProcessingRequired);

            //CapCondition
            if (CapCondition == null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                Serializer.Serialize(writer.BaseStream, CapCondition);
            }

            //GetIndexHeaderType
            writer.Write((byte)GetIndexHeaderType);

            //DomainSpecificProcessingType
            writer.Write((byte)DomainSpecificProcessingType);

            //GroupBy
            if (GroupBy == null)
            {
                writer.Write(false);
            }
            else
            {
                writer.Write(true);
                Serializer.Serialize(writer.BaseStream, GroupBy);
            }
        }

        public override void Deserialize(IPrimitiveReader reader, int version)
        {
            //Offset
            Offset = reader.ReadInt32();

            //Span
            Span = reader.ReadInt32();

            //TargetIndexName
            TargetIndexName = reader.ReadString();

            //TagsFromIndexes
            ushort count = reader.ReadUInt16();
            if (count > 0)
            {
                TagsFromIndexes = new List<string>(count);
                for (ushort i = 0; i < count; i++)
                {
                    TagsFromIndexes.Add(reader.ReadString());
                }
            }

            //TagSort
            if (reader.ReadByte() != 0)
            {
                TagSort = new TagSort();
                Serializer.Deserialize(reader.BaseStream, TagSort);
            }

            //IndexIdList
            count = reader.ReadUInt16();
            if (count > 0)
            {
                IndexIdList = new List<byte[]>(count);
                ushort len;
                for (ushort i = 0; i < count; i++)
                {
                    len = reader.ReadUInt16();
                    if (len > 0)
                    {
                        IndexIdList.Add(reader.ReadBytes(len));
                    }
                }
            }

            //Read a byte to account for deprecated CriterionList
            reader.ReadByte();

            //MaxItemsPerIndex
            MaxItems = reader.ReadInt32();

            //ExcludeData
            ExcludeData = reader.ReadBoolean();

            //GetIndexHeader
            GetIndexHeader = reader.ReadBoolean();

            //GetPageableItemCount
            GetAdditionalAvailableItemCount = reader.ReadBoolean();

            //PrimaryIdList
            count = reader.ReadUInt16();
            if (count > 0)
            {
                PrimaryIdList = new List<int>(count);
                for (ushort i = 0; i < count; i++)
                {
                    PrimaryIdList.Add(reader.ReadInt32());
                }
            }

            //Filter
            byte b = reader.ReadByte();
            if (b != 0)
            {
                FilterType filterType = (FilterType)b;
                Filter = FilterFactory.CreateFilter(reader, filterType);
            }

            //IndexIdParamsMapping
            count = reader.ReadUInt16();
            if (count > 0)
            {
                IndexIdParamsMapping = new Dictionary<byte[], IndexIdParams>(count, new ByteArrayEqualityComparer());
                byte[] indexId;
                IndexIdParams indexIdParam;
                ushort len;

                for (ushort i = 0; i < count; i++)
                {
                    len = reader.ReadUInt16();
                    indexId = null;
                    if (len > 0)
                    {
                        indexId = reader.ReadBytes(len);

                        indexIdParam = new IndexIdParams();
                        Serializer.Deserialize(reader.BaseStream, indexIdParam);

                        IndexIdParamsMapping.Add(indexId, indexIdParam);
                    }
                }
            }

            //FullDataIdInfo
            if (reader.ReadBoolean())
            {
                FullDataIdInfo = new FullDataIdInfo();
                Serializer.Deserialize(reader.BaseStream, FullDataIdInfo);
            }

            //ClientSideSubsetProcessingRequired
            ClientSideSubsetProcessingRequired = reader.ReadBoolean();

            if (version >= 2)
            {
                //CapCondition
                if (reader.ReadBoolean())
                {
                    CapCondition = new CapCondition();
                    Serializer.Deserialize(reader.BaseStream, CapCondition);
                }
            }

            if (version >= 3)
            {
                //GetIndexHeaderType
                GetIndexHeaderType = (GetIndexHeaderType)reader.ReadByte();
            }

            if (version >= 4)
            {
                //DomainSpecificProcessingType
                DomainSpecificProcessingType = (DomainSpecificProcessingType)reader.ReadByte();
            }

            if (version >= 5)
            {
                //GroupBy
                if (reader.ReadBoolean())
                {
                    GroupBy = new GroupBy();
                    Serializer.Deserialize(reader.BaseStream, GroupBy);
                }
            }
        }

        private const int CURRENT_VERSION = 5;
        public override int CurrentVersion
        {
            get
            {
                return CURRENT_VERSION;
            }
        }

        #endregion

        #region ICloneable Members

        public sealed override object Clone()
        {
            return new SpanQuery(this);
        }

        #endregion
    }
}
