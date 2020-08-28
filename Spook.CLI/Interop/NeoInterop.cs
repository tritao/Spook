﻿using System;
using System.Collections.Generic;
using LunarLabs.Parser;
using PBigInteger = Phantasma.Numerics.BigInteger;
using System.Numerics;
using Phantasma.Blockchain;
using Phantasma.Neo.Core;
using Phantasma.Neo.Utils;
using Phantasma.Neo.Cryptography;
using NeoBlock = Phantasma.Neo.Core.Block;
using NeoTx = Phantasma.Neo.Core.Transaction;
using Phantasma.Domain;
using Phantasma.Pay;
using Phantasma.Pay.Chains;
using Phantasma.Cryptography;
using Phantasma.Spook.Swaps;
using Phantasma.Core.Log;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Threading;

namespace Phantasma.Spook.Interop
{
    public class NeoInterop : ChainWatcher
    {
        private Logger logger;
        private NeoAPI neoAPI;
        private BigInteger _interopBlockHeight;
        private OracleReader oracleReader;
        private DateTime lastScan;
        private static bool initialStart = true;
        private bool quickSync = false;

        public static Dictionary<string, CryptoCurrencyInfo> NeoTokenInfo = new Dictionary<string, CryptoCurrencyInfo>()
        {
            // symbol name dec plat caps
            { "NEO", new CryptoCurrencyInfo("NEO", "NEO", 0, NeoWallet.NeoPlatform, CryptoCurrencyCaps.Balance) },
            { "GAS", new CryptoCurrencyInfo("GAS", "GAS", 8, NeoWallet.NeoPlatform, CryptoCurrencyCaps.Balance) },
            { "SOUL", new CryptoCurrencyInfo("SOUL", "Phantasma Stake", 8, NeoWallet.NeoPlatform, CryptoCurrencyCaps.Balance) },
        };

        public NeoInterop(TokenSwapper swapper, NeoAPI neoAPI, string wif, PBigInteger interopBlockHeight
            ,OracleReader oracleReader, bool quickSync, Logger logger)
                : base(swapper, wif, "neo")
        {
            string lastBlockHeight = oracleReader.GetCurrentHeight("neo", "neo");

            this._interopBlockHeight = (!string.IsNullOrEmpty(lastBlockHeight)) 
                                       ? BigInteger.Parse(lastBlockHeight) 
                                       : new BigInteger(interopBlockHeight.ToUnsignedByteArray());
            this.quickSync = quickSync;

            logger.Message($"interopHeight: {_interopBlockHeight}");
            this.neoAPI = neoAPI;

            this.oracleReader = oracleReader;

            this.lastScan = DateTime.UtcNow.AddYears(-1);;

            this.logger = logger;
        }

        protected override string GetAvailableAddress(string wif)
        {
            var neoKeys = Phantasma.Neo.Core.NeoKeys.FromWIF(wif);
            return neoKeys.Address;
        }

        public override IEnumerable<PendingSwap> Update()
        {
            lock (String.Intern("PendingSetCurrentHeight_" + "neo"))
            {
                var result = new List<PendingSwap>();

                // initial start, we have to verify all processed swaps
                if (initialStart)
                {
                    // TODO check if quick sync nodes are configured, if so use quick sync
                    // we need to find a better solution for that though
                    var allInteropBlocks = oracleReader.ReadAllBlocks("neo", "neo");

                    logger.Message($"Found {allInteropBlocks.Count} blocks");

                    foreach (var block in allInteropBlocks)
                    {
                        ProcessBlock(block, result);
                    }

                    initialStart = false;

                    // return after the initial start to be able to process all swaps that happend in the mean time.
                    return result;
                }

                if (quickSync)
                {
                    // if quick sync is active, we can use a specific plugin installed on the nodes (EventTracker)
                    // TODO reversed contract hash is hardcoded for now, very unlikely to change.
                    var blockIds = neoAPI.GetSwapBlocks("b3a766ac60afa2990d9251db08138fd1facf07ed", LocalAddress, this._interopBlockHeight.ToString());
                    var batchCount = 8;
                    List<Task<InteropBlock>> taskList = CreateTaskList(batchCount, blockIds.Values.ToArray());

                    foreach (var task in taskList)
                    {
                        task.Start();
                    }

                    Task.WaitAll(taskList.ToArray());

                    // get blocks and order them for processing
                    var blocksToProcess = taskList.Select(x => x.Result).ToList()
                            .Where(x => blockIds.ContainsKey(x.Hash.ToString()))
                            .Select(x => new { block = x, id = blockIds[x.Hash.ToString()] })
                            .OrderBy(x => x.id);

                    logger.Message($"blocksToProcess: {blocksToProcess.Count()}");

                    foreach (var entry in blocksToProcess)
                    {
                        ProcessBlock(entry.block, result);
                        oracleReader.SetCurrentHeight("neo", "neo", _interopBlockHeight.ToString());
                        _interopBlockHeight = BigInteger.Parse(entry.id.ToString());
                    }
                }
                else
                {
                    var blockIterator = new BlockIterator(neoAPI);
                    var blockDifference = blockIterator.currentBlock - _interopBlockHeight;
                    var batchCount = (blockDifference > 8) ? 8 : blockDifference; //TODO make it a constant, should be no more than 8

                    while (blockIterator.currentBlock > _interopBlockHeight)
                    {
                        logger.Message("==== current: " + blockIterator.currentBlock + " interop: " + _interopBlockHeight);
                        //logger.Message("start======================= " + DateTime.Now.ToString("yyyy’-‘MM’-‘dd’ ’HH’:’mm’:’ss.fff"));
                        if (batchCount > 1)
                        {
                            List<Task<InteropBlock>> taskList = CreateTaskList(batchCount);

                            foreach (var task in taskList)
                            {
                                task.Start();
                            }

                            Task.WaitAll(taskList.ToArray());

                            foreach (var task in taskList)
                            {
                                var block = task.Result;

                                ProcessBlock(block, result);
                            }

                            oracleReader.SetCurrentHeight("neo", "neo", _interopBlockHeight.ToString());
                            _interopBlockHeight += batchCount;
                        }
                        else
                        {
                            var url = DomainExtensions.GetOracleBlockURL(
                                    "neo", "neo", PBigInteger.FromUnsignedArray(_interopBlockHeight.ToByteArray(), true));

                            var interopBlock = oracleReader.Read<InteropBlock>(DateTime.Now, url);

                            ProcessBlock(interopBlock, result);

                            oracleReader.SetCurrentHeight("neo", "neo", _interopBlockHeight.ToString());
                            _interopBlockHeight++;
                        }
                    }
                }
                return result;
            }
        }

        private List<Task<InteropBlock>> CreateTaskList(BigInteger batchCount, BigInteger[] blockIds = null)
        {
            List<Task<InteropBlock>> taskList = new List<Task<InteropBlock>>();
            if (blockIds == null)
            {
                var nextCurrentBlockHeight = _interopBlockHeight + batchCount;
                
                for (var i = _interopBlockHeight; i < nextCurrentBlockHeight; i++)
                {
                    var url = DomainExtensions.GetOracleBlockURL(
                            "neo", "neo", PBigInteger.FromUnsignedArray(i.ToByteArray(), true));
                
                    taskList.Add(CreateTask(url));
                }
            }
            else
            {
                foreach (var blockId in blockIds)
                {
                    var url = DomainExtensions.GetOracleBlockURL(
                            "neo", "neo", PBigInteger.FromUnsignedArray(blockId.ToByteArray(), true));
                    taskList.Add(CreateTask(url));
                }
            }

            return taskList;
        }

        private Task<InteropBlock> CreateTask(string url)
        {
            return new Task<InteropBlock>(() =>
                   {
                       var delay = 1000;

                       while (true)
                       {
                           try
                           {
                               return oracleReader.Read<InteropBlock>(DateTime.Now, url);
                           }
                           catch (Exception e)
                           {
                               var logMessage = "oracleReader.Read() exception caught:\n" + e.Message;
                               var inner = e.InnerException;
                               while (inner != null)
                               {
                                   logMessage += "\n---> " + inner.Message + "\n\n" + inner.StackTrace;
                                   inner = inner.InnerException;
                               }
                               logMessage += "\n\n" + e.StackTrace;

                               logger.Message(logMessage.Contains("Neo block is null") ? "oracleReader.Read(): Neo block is null, possible connection failure" : logMessage);
                           }

                           Thread.Sleep(delay);
                           if (delay >= 60000) // Once we reach 1 minute, we stop increasing delay and just repeat every minute.
                               delay = 60000;
                           else
                               delay *= 2;
                       }
                   });
        }

        private void ProcessBlock(InteropBlock block, List<PendingSwap> result)
        {
            foreach (var txHash in block.Transactions)
            {
                var interopTx = oracleReader.ReadTransaction("neo", "neo", txHash);

                // TODO check why
                if (interopTx.Transfers.Length != 1)
                {
                    throw new OracleException("neo transfers with multiple sources or tokens not supported yet");
                }

                foreach (var interopTransfer in interopTx.Transfers)
                {
                    result.Add(
                                new PendingSwap(
                                                 this.PlatformName
                                                ,txHash
                                                ,interopTransfer.sourceAddress
                                                ,interopTransfer.interopAddress)
                            );
                }
            }
        }

        private static string FindSymbolFromAsset(string assetID)
        {
            if (assetID.StartsWith("0x"))
            {
                assetID = assetID.Remove(0,2) ;
            }
            //logger.Message("asset.... " + assetID);
            switch (assetID)
            {
                case "b3a766ac60afa2990d9251db08138fd1facf07ed": return "SOUL";
                case "ed07cffad18f1308db51920d99a2af60ac66a7b3": return "SOUL"; // ugly needs change
                case "c56f33fc6ecfcd0c225c4ab356fee59390af8560be0e930faebe74a6daff7c9b": return "NEO";
                case "602c79718b16e442de58778e148d0b1084e3b2dffd5de6b7b16cee7969282de7": return "GAS";
                default: return null;
            }
        }

        public static Tuple<InteropBlock, InteropTransaction[]> MakeInteropBlock(Logger logger, NeoBlock block, NeoAPI api, string swapAddress)
        {
            List<Hash> hashes = new List<Hash>();

            // if the block has no swap tx, it's currently not of interest
            bool blockOfInterest = false;
            List<InteropTransaction> interopTransactions = new List<InteropTransaction>();
            foreach (var tx in block.transactions)
            {
                if (tx.type == TransactionType.InvocationTransaction
                    || tx.type == TransactionType.ContractTransaction)
                {
                    var interopTx = MakeInteropTx(logger, tx, api, swapAddress);
                    if (interopTx.Hash != Hash.Null)
                    {
                        interopTransactions.Add(interopTx);
                        hashes.Add(Hash.FromBytes(tx.Hash.ToArray()));
                        blockOfInterest = true;
                    }
                }
            }

            InteropBlock iBlock = (blockOfInterest)
                ? new InteropBlock("neo", "neo", Hash.FromBytes(block.Hash.ToArray()), hashes.ToArray())
                : new InteropBlock("neo", "neo", Hash.Null, hashes.ToArray());

            return Tuple.Create(iBlock, interopTransactions.ToArray());
        }

        public static InteropTransaction MakeInteropTx(Logger logger, NeoTx tx, NeoAPI api, string swapAddress)
        {
            logger.Message("checking tx: " + tx.Hash);

            List<InteropTransfer> interopTransfers = new List<InteropTransfer>();

            var emptyTx = new InteropTransaction(Hash.Null, interopTransfers.ToArray());

            PBigInteger amount;

            if (tx.witnesses.Length != 1)
            {
                //currently only one witness allowed
                return emptyTx;
            }

            var sourceScriptHash = tx.witnesses[0].verificationScript.Sha256().RIPEMD160();
            var sourceAddress = NeoWallet.EncodeByteArray(sourceScriptHash);
            var interopAddress = tx.witnesses[0].ExtractAddress();
            var interopSwapAddress = NeoWallet.EncodeAddress(swapAddress);

            logger.Message("interop address: " + interopAddress);
            //logger.Message("xswapAddress: " + swapAddress);
            //logger.Message("interop sourceAddress: " + sourceAddress);
            //logger.Message("neo sourceAddress: " + NeoWallet.DecodeAddress(sourceAddress));

            if (tx.attributes != null && tx.attributes.Length > 0)
            {
                foreach(var attr in tx.attributes)
                {
                    if (attr.Usage == TransactionAttributeUsage.Description)
                    {
                        try
                        {
                            var text = Encoding.UTF8.GetString(attr.Data);
                            if (Address.IsValidAddress(text))
                            {
                                interopAddress = Address.FromText(text);
                                logger.Message("new interop address: " + interopAddress);
                            }
                        }
                        catch {}
                    }
                }
            }

            if (tx.outputs.Length > 0)
            {
                foreach (var output in tx.outputs)
                {
                    //logger.Message("have outputs");
                    var targetAddress = NeoWallet.EncodeByteArray(output.scriptHash.ToArray());
                    //logger.Message("interop targetAddress : " + targetAddress);
                    //logger.Message("neo targetAddress: " + NeoWallet.DecodeAddress(targetAddress));
                    logger.Message("interopSwapAddress: " + interopSwapAddress);
                    logger.Message("targetAddress: " + targetAddress);

                    var swpAddress = NeoWallet.EncodeAddress(swapAddress);
                    //logger.Message("interop swpAddress: " + swpAddress);
                    //logger.Message("neo swpAddress: " + NeoWallet.DecodeAddress(swpAddress));
                    //if (targetAddress.ToString() == swapAddress)
                    if (interopSwapAddress == targetAddress)
                    {
                        var token = FindSymbolFromAsset(new UInt256(output.assetID).ToString());
                        CryptoCurrencyInfo tokenInfo;
                        if (NeoTokenInfo.TryGetValue(token, out tokenInfo))
                        {
                            amount = Phantasma.Numerics.UnitConversion.ToBigInteger(
                                    output.value, tokenInfo.Decimals);
                        }
                        else
                        {
                            // asset not swapable at the moment...
                            logger.Message("Asset not swapable");
                            return emptyTx;
                        }

                        logger.Message("UTXO " + amount);
                        interopTransfers.Add
                        (
                            new InteropTransfer
                            (
                                NeoWallet.NeoPlatform,
                                sourceAddress,
                                DomainSettings.PlatformName,
                                targetAddress,
                                interopAddress, // interop address
                                token.ToString(),
                                amount
                            )
                        );
                    }
                }
            }

            if (tx.script != null && tx.script.Length > 0) // NEP5 transfers
            {
                var script = NeoDisassembler.Disassemble(tx.script, true);

                logger.Message("SCRIPT ====================");
                foreach (var entry in script.lines)
                {
                    logger.Message($"{entry.name} : { entry.opcode }");
                }
                logger.Message("SCRIPT ====================");

                if (script.lines.Count() < 7)
                {
                    logger.Message("NO SCRIPT!!!!");
                    return emptyTx;
                }

                var disasmEntry = script.lines.ElementAtOrDefault(6);

                //if ( disasmEntry == null )
                //{
                //    logger.Message("disasmEntry is null");
                //}
                //if ( disasmEntry != null )
                //{
                //    if ( disasmEntry.data == null)
                //        logger.Message("disasmEntry.data is 0");
                //}

                if (disasmEntry.name != "APPCALL" || disasmEntry.data == null ||  disasmEntry.data.Length == 0)
                {
                    logger.Message("NO APPCALL");
                    return emptyTx;
                }
                else
                {
                    
                    var assetString = new UInt160(disasmEntry.data).ToString();
                    logger.Message("ASSET::::::::::::: " + assetString);
                    if (string.IsNullOrEmpty(assetString) || FindSymbolFromAsset(assetString) == null)
                    {
                        logger.Message("Ignore TX due to non swapable token.");
                        return emptyTx;
                    }
                }

                int pos = 0;
                foreach (var entry in script.lines)
                {
                    pos++;
                    if (pos > 3)
                    {
                        // we are only interested in the first three elements
                        break;
                    }

                    if (pos == 1)
                    {
                        amount = PBigInteger.FromUnsignedArray(entry.data, true);
                    }
                    if (pos == 2 || pos == 3)
                    {
                        if (pos ==2)
                        {
                            var targetScriptHash = new UInt160(entry.data);
                            logger.Message("neo targetAddress: " + targetScriptHash.ToAddress());
                            var targetAddress = NeoWallet.EncodeByteArray(entry.data);
                            logger.Message("targetAddress : " + targetAddress);
                            logger.Message("targetAddress2: " + interopSwapAddress);
                            logger.Message("ySwapAddress: " + swapAddress);
                            if (interopSwapAddress == targetAddress)
                            {
                                // found a swap, call getapplicationlog now to get transaction details and verify the tx was actually processed.
                                ApplicationLog[] appLogs = api.GetApplicationLog(tx.Hash);
                                for (var i = 0; i < appLogs.Length; i++)
                                {
                                    logger.Message("appLogs[i].contract" + appLogs[i].contract);
                                    var token = FindSymbolFromAsset(appLogs[i].contract);
                                    logger.Message("TOKEN::::::::::::::::::: " + token);
                                    logger.Message("amount: " + appLogs[i].amount + " " + token);
                                    var sadd = NeoWallet.EncodeByteArray(appLogs[i].sourceAddress.ToArray());
                                    var tadd = NeoWallet.EncodeByteArray(appLogs[i].targetAddress.ToArray());


                                    interopTransfers.Add
                                    (
                                        new InteropTransfer
                                        (
                                            "neo", // todo Pay.Chains.NeoWallet.NeoPlatform
                                            //NeoWallet.EncodeByteArray(appLogs[i].sourceAddress.ToArray()),
                                            sourceAddress,
                                            DomainSettings.PlatformName,
                                            targetAddress,
                                            interopAddress, // interop address
                                            token,
                                            appLogs[i].amount
                                        )
                                    );
                                }
                            }
                        }
                        else
                        {
                            //TODO reverse swap
                            sourceScriptHash = new UInt160(entry.data).ToArray();
                            sourceAddress = NeoWallet.EncodeByteArray(sourceScriptHash.ToArray());
                        }
                    }
                }
            }

            return ((interopTransfers.Count() > 0)
                ? new InteropTransaction(Hash.FromBytes(tx.Hash.ToArray()), interopTransfers.ToArray())
                : new InteropTransaction(Hash.Null, interopTransfers.ToArray()));
        }
    }
}
