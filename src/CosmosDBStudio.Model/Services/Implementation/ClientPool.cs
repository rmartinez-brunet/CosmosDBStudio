﻿using System;
using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace CosmosDBStudio.Model.Services.Implementation
{
    public class ClientPool : IClientPool
    {
        private readonly ConcurrentDictionary<string, CosmosClient> _clients;

        public ClientPool()
        {
            _clients = new ConcurrentDictionary<string, CosmosClient>();
        }

        public CosmosClient GetClientForAccount(CosmosAccount account)
        {
            if (account is null) throw new ArgumentNullException(nameof(account));

            return _clients.GetOrAdd(account.Id, _ => CreateClient(account));
        }

        public void RemoveClientForAccount(CosmosAccount account)
        {
            if (account is null) throw new ArgumentNullException(nameof(account));

            if (_clients.TryRemove(account.Id, out var client))
                client.Dispose();
        }

        private CosmosClient CreateClient(CosmosAccount account)
            => string.IsNullOrEmpty(account.Key)
                ? new CosmosClient(account.Endpoint, new DefaultAzureCredential())
                : new CosmosClient(account.Endpoint, account.Key);
        
    }
}
