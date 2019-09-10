﻿using System;
using System.Collections.Generic;
using LunarLabs.Parser.JSON;
using Phantasma.Blockchain;
using Phantasma.Blockchain.Contracts;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Cryptography;
using Phantasma.Storage;
using Phantasma.Numerics;
using Phantasma.Pay.Chains;
using Phantasma.Core;

namespace Phantasma.Spook.Oracles
{
    public class NeoScanAPI
    {
        public readonly string URL;

        private readonly Address chainAddress;
        private static readonly string chainName = "NEO";

        public NeoScanAPI(string url, KeyPair keys)
        {
            this.URL = url;

            var key = InteropUtils.GenerateInteropKeys(keys, "NEO");
            this.chainAddress = key.Address;
        }

        public byte[] ReadOracle(string[] input)
        {
            if (input == null || input.Length != 2)
            {
                throw new OracleException("missing oracle input");
            }

            var cmd = input[0].ToLower();
            switch (cmd)
            {
                case "tx":
                    return ReadTransaction(input[1]);

                case "block":
                    return ReadBlock(input[1]);

                default:
                    throw new OracleException("unknown neo oracle");
            }
        }

        private static byte[] PackEvent(object content)
        {
            var bytes = content == null ? new byte[0] : Serialization.Serialize(content);
            return bytes;
        }

        public string GetRequestURL(string request)
        {
            Throw.If(request.StartsWith("/"), "request malformed");
            return $"https://api.{URL}/api/main_net/v1/{request}";
        }

        public byte[] ReadTransaction(string hashText)
        {
            if (hashText.StartsWith("0x"))
            {
                hashText = hashText.Substring(2);
            }

            var url = GetRequestURL($"get_transaction/{hashText}");

            string json;

            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    json = wc.DownloadString(url);
                }

                var tx = new InteropTransaction();
                tx.ChainName = chainName;
                tx.Hash = Hash.Parse(hashText);

                var root = JSONReader.ReadFromString(json);

                var eventList = new List<Event>();

                var vins = root.GetNode("vin");
                Throw.IfNull(vins, nameof(vins));

                string inputAsset = null;
                string inputSource = null;

                BigInteger inputAmount = 0;

                foreach (var input in vins.Children)
                {
                    var addrText = input.GetString("address_hash");
                    if (inputSource == null)
                    {
                        inputSource = addrText;
                    }
                    else
                    if (inputSource != addrText)
                    {
                        throw new OracleException("transaction with multiple input sources, unsupported for now");
                    }

                    var assetSymbol = input.GetString("asset");

                    if (inputAsset == null)
                    {
                        inputAsset = assetSymbol;
                    }
                    else
                    if (inputAsset != assetSymbol)
                    {
                        throw new OracleException("transaction with multiple input assets, unsupported for now");
                    }
                }

                if (inputAsset == null || inputSource == null || inputAmount <= 0)
                {
                    throw new OracleException("transaction with invalid inputs, something failed");
                }

                var vouts = root.GetNode("vouts");
                Throw.IfNull(vouts, nameof(vouts));

                foreach (var output in vouts.Children)
                {
                    var addrText = output.GetString("address_hash");

                    var assetSymbol = output.GetString("asset");
                    var destination = NeoWallet.EncodeAddress(addrText);
                    var value = output.GetFloat("value");
                    value *= (float)Math.Pow(10, 8);
                    var amount = new BigInteger((long)value);

                    if (addrText == inputSource)
                    {
                        inputAmount -= amount;
                        continue;
                    }

                    var evt = new Event(EventKind.TokenReceive, destination, PackEvent(new TokenEventData() { chainAddress = chainAddress, value = amount, symbol = assetSymbol }));
                    eventList.Add(evt);
                }

                var source = NeoWallet.EncodeAddress(inputSource);
                var sendEvt = new Event(EventKind.TokenSend, source, PackEvent(new TokenEventData() { chainAddress = chainAddress, value = inputAmount, symbol = inputAsset }));
                eventList.Add(sendEvt);

                tx.Events = eventList.ToArray();
                return Serialization.Serialize(tx);
            }
            catch (Exception e)
            {
                throw new OracleException(e.Message);
            }
        }

        public byte[] ReadBlock(string blockText)
        {
            if (blockText.StartsWith("0x"))
            {
                blockText = blockText.Substring(2);
            }

            var url = GetRequestURL($"get_block/{blockText}");

            string json;

            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    json = wc.DownloadString(url);
                }

                var block = new InteropBlock();
                block.ChainName = chainName;
                block.Hash = Hash.Parse(blockText);

                var root = JSONReader.ReadFromString(json);

                var transactions = root.GetNode("transactions");
                var hashes = new List<Hash>();

                foreach (var entry in transactions.Children)
                {
                    var hash = Hash.Parse(entry.Value);
                    hashes.Add(hash);
                }

                block.Transactions = hashes.ToArray();
                return Serialization.Serialize(block);
            }
            catch (Exception e)
            {
                throw new OracleException(e.Message);
            }
        }
    }
}
