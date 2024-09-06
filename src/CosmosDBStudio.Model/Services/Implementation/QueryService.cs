﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace CosmosDBStudio.Model.Services.Implementation
{
    public class QueryService : IQueryService
    {
        private readonly Func<Container> _containerGetter;

        public QueryService(Func<Container> containerGetter)
        {
            _containerGetter = containerGetter;
        }

        public async Task<QueryResult> ExecuteAsync(Query query, string? continuationToken, CancellationToken cancellationToken)
        {
            var queryDefinition = CreateQueryDefinition(query);
            var requestOptions = CreateRequestOptions(query);

            var iterator = _containerGetter().GetItemQueryIterator<JToken>(queryDefinition, continuationToken, requestOptions);
            
            var result = new QueryResult(query);


            var stopwatch = new Stopwatch();
            
            List<string>? warnings = null;
            
            List<JToken> items = new();
            try
            {
                stopwatch.Start();
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync();
                    stopwatch.Stop();
                    result.RequestCharge += response.RequestCharge;
                    try
                    {
                        result.ContinuationToken = response.ContinuationToken;
                    }
                    catch (Exception ex)
                    {
                        warnings ??= new List<string>();
                        warnings.Add(ex.Message);
                    }

                    items.AddRange(response.ToList());
                }
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }
            finally
            {
                result.Items = items;

                result.Warnings = warnings ?? Enumerable.Empty<string>();
                stopwatch.Stop();
            }

            result.TimeElapsed = stopwatch.Elapsed;
            return result;
        }

        private static QueryDefinition CreateQueryDefinition(Query query)
        {
            var definition = new QueryDefinition(query.Sql);
            if (query.Parameters != null)
            {
                foreach (var (key, value) in query.Parameters)
                {
                    definition = definition.WithParameter(key, value);
                }
            }

            return definition;
        }

        private static QueryRequestOptions CreateRequestOptions(Query query)
        {
            return new QueryRequestOptionsBuilder()
                .WithPartitionKey(query.PartitionKey)
                .WithMaxItemCount(100)
                .Build();
        }
    }
}
