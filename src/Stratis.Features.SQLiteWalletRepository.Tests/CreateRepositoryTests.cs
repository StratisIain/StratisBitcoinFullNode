﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DBreeze.DataTypes;
using NBitcoin;
using NBitcoin.Protocol;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.Wallet;
using Stratis.Bitcoin.Tests.Common;
using Stratis.Bitcoin.Utilities;
using Xunit;

namespace Stratis.Features.SQLiteWalletRepository.Tests
{
    public class TempDataFolder : DataFolder, IDisposable
    {
        private static string ClassNameFromFileName(string classOrFileName)
        {
            string className = classOrFileName.Substring(classOrFileName.LastIndexOf('\\') + 1);

            return className.Split(".")[0];
        }

        public TempDataFolder([CallerFilePath] string classOrFileName = "", [CallerMemberName] string callingMethod = "")
            : base(TestBase.AssureEmptyDir(TestBase.GetTestDirectoryPath(Path.Combine(ClassNameFromFileName(classOrFileName), callingMethod))))
        {
            try
            {
                Directory.Delete(TestBase.GetTestDirectoryPath(ClassNameFromFileName(classOrFileName)), true);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
        }
    }

    public class MultiWalletRepositoryTests : RepositoryTests
    {
        public MultiWalletRepositoryTests() : base(false)
        {
        }
    }

    public class RepositoryTests
    {
        private Network network;
        private readonly bool dbPerWallet;
        private readonly string dataDir;
        private string walletName;

        public RepositoryTests(bool dbPerWallet = true)
        {
            this.dbPerWallet = dbPerWallet;
            this.network = KnownNetworks.StratisTest;

            // Configure this to point to your "StratisTest" root folder and wallet.
            this.walletName = "test2";
            this.dataDir = @"E:\RunNodes\SideChains\Data\MainchainUser";
        }

        [Fact]
        public void CanCreateWalletAndTransactionsAndAddressesAndCanRewind()
        {
            using (var dataFolder = new TempDataFolder(this.GetType().Name))
            {
                var repo = new SQLiteWalletRepository(dataFolder, this.network, DateTimeProvider.Default, new ScriptAddressReader(), new ScriptPubKeyProvider());

                repo.Initialize(this.dbPerWallet);

                var account = new WalletAccountReference(this.walletName, "account 0");

                // Bypass IsExtPubKey wallet check.
                var nodeSettings = new NodeSettings(this.network, args: new[] { $"-datadir={this.dataDir}" }, protocolVersion: ProtocolVersion.ALT_PROTOCOL_VERSION);
                Wallet wallet = new FileStorage<Wallet>(nodeSettings.DataFolder.WalletPath).LoadByFileName($"{this.walletName}.wallet.json");

                // Create an "test2" as an empty wallet.
                byte[] chainCode = wallet.ChainCode;
                repo.CreateWallet(account.WalletName, wallet.EncryptedSeed, chainCode);

                // Verify the wallet exisits.
                Assert.Equal(this.walletName, repo.GetWalletNames().First());

                // Create "account 0" as P2PKH.
                foreach (HdAccount hdAccount in wallet.GetAccounts(a => a.Name == account.AccountName))
                {
                    var extPubKey = ExtPubKey.Parse(hdAccount.ExtendedPubKey);
                    repo.CreateAccount(this.walletName, hdAccount.Index, hdAccount.Name, extPubKey, "P2PKH");
                }

                // Create block 1.
                Block block1 = this.network.Consensus.ConsensusFactory.CreateBlock();
                BlockHeader blockHeader1 = block1.Header;

                // Create transaction 1.
                Transaction transaction1 = this.network.CreateTransaction();

                // Send 100 coins to the first unused address in the wallet.
                HdAddress address = repo.GetUnusedAddresses(account, 1).FirstOrDefault();
                transaction1.Outputs.Add(new TxOut(Money.COIN * 100, address.ScriptPubKey));

                // Add transaction 1 to block 1.
                block1.Transactions.Add(transaction1);

                // Process block 1.
                var chainedHeader1 = new ChainedHeader(blockHeader1, blockHeader1.GetHash(), null);
                repo.ProcessBlock(block1, chainedHeader1, account.WalletName);

                // List the unspent outputs.
                List<UnspentOutputReference> outputs1 = repo.GetSpendableTransactionsInAccount(account, chainedHeader1, 0).ToList();
                Assert.Single(outputs1);
                Assert.Equal(Money.COIN * 100, (long)outputs1[0].Transaction.Amount);

                // Create block 2.
                Block block2 = this.network.Consensus.ConsensusFactory.CreateBlock();
                BlockHeader blockHeader2 = block2.Header;
                blockHeader2.HashPrevBlock = blockHeader1.GetHash();

                // Create transaction 2.
                Transaction transaction2 = this.network.CreateTransaction();

                // Send the 90 coins to a fictituous external address.
                Script dest = PayToPubkeyHashTemplate.Instance.GenerateScriptPubKey(new KeyId());
                transaction2.Inputs.Add(new TxIn(new OutPoint(transaction1.GetHash(), 0)));
                transaction2.Outputs.Add(new TxOut(Money.COIN * 90, dest));

                // Send 9 coins change to my first unused change address.
                HdAddress changeAddress = repo.GetUnusedAddresses(account, 1, true).FirstOrDefault();
                transaction2.Outputs.Add(new TxOut(Money.COIN * 9, changeAddress.ScriptPubKey));

                // Add transaction 2 to block 2.
                block2.Transactions.Add(transaction2);

                // Process block 2.
                var chainedHeader2 = new ChainedHeader(blockHeader2, blockHeader2.HashPrevBlock, chainedHeader1);
                repo.ProcessBlock(block2, chainedHeader2, account.WalletName);

                // List the unspent outputs.
                List<UnspentOutputReference> outputs2 = repo.GetSpendableTransactionsInAccount(account, chainedHeader2, 0).ToList();
                Assert.Single(outputs2);
                Assert.Equal(Money.COIN * 9, (long)outputs2[0].Transaction.Amount);

                // Check the wallet history.
                List<AccountHistory> accountHistories = repo.GetHistory(account.WalletName, account.AccountName).ToList();
                Assert.Single(accountHistories);
                List<FlatHistory> history = accountHistories[0].History.ToList();
                Assert.Equal(2, history.Count);

                // Verify 100 coins sent to first unused external address in the wallet.
                Assert.Equal(address.Address, history[0].Address.Address);
                Assert.Equal(address.HdPath, history[0].Address.HdPath);
                Assert.Equal(0, history[0].Address.Index);
                Assert.Equal(Money.COIN * 100, (long)history[0].Transaction.Amount);

                // Looking at the spending tx we see 90 coins sent out and 9 sent to internal change address.
                List<PaymentDetails> payments = history[0].Transaction.SpendingDetails.Payments.ToList();
                Assert.Equal(2, payments.Count);
                Assert.Equal(Money.COIN * 90, (long)payments[0].Amount);
                Assert.Equal(dest, payments[0].DestinationScriptPubKey);
                Assert.Equal(Money.COIN * 9, (long)payments[1].Amount);
                Assert.Equal(changeAddress.ScriptPubKey, payments[1].DestinationScriptPubKey);

                // Verify 9 coins sent to first unused change address in the wallet.
                Assert.Equal(changeAddress.Address, history[1].Address.Address);
                Assert.Equal(changeAddress.HdPath, history[1].Address.HdPath);
                Assert.Equal(0, history[1].Address.Index);
                Assert.Equal(Money.COIN * 9, (long)history[1].Transaction.Amount);

                // REWIND: Remove block 1.
                repo.RewindWallet("test2", chainedHeader1);

                // List the unspent outputs.
                outputs1 = repo.GetSpendableTransactionsInAccount(account, chainedHeader1, 0).ToList();
                Assert.Single(outputs1);
                Assert.Equal(Money.COIN * 100, (long)outputs1[0].Transaction.Amount);

                // Check the wallet history.
                List<AccountHistory> accountHistories2 = repo.GetHistory(account.WalletName, account.AccountName).ToList();
                Assert.Single(accountHistories2);
                List<FlatHistory> history2 = accountHistories2[0].History.ToList();
                Assert.Single(history2);

                // Verify 100 coins sent to first unused external address in the wallet.
                Assert.Equal(address.Address, history2[0].Address.Address);
                Assert.Equal(address.HdPath, history2[0].Address.HdPath);
                Assert.Equal(0, history2[0].Address.Index);
                Assert.Equal(Money.COIN * 100, (long)history2[0].Transaction.Amount);

                // Verify that the spending details have been removed.
                Assert.Null(history2[0].Transaction.SpendingDetails);
            }
        }

        [Fact(Skip = "Configure this test then run it manually. Comment this Skip.")]
        public void CanProcessBlocks()
        {
            using (var dataFolder = new TempDataFolder(this.GetType().Name))
            {
                // Set up block store.
                var nodeSettings = new NodeSettings(this.network, args: new[] { $"-datadir={this.dataDir}" }, protocolVersion: ProtocolVersion.ALT_PROTOCOL_VERSION);
                DBreezeSerializer serializer = new DBreezeSerializer(this.network.Consensus.ConsensusFactory);

                // Build the chain from the block store.
                IBlockRepository blockRepo = new BlockRepository(this.network, nodeSettings.DataFolder, nodeSettings.LoggerFactory, serializer);
                blockRepo.Initialize();

                var prevBlock = new Dictionary<uint256, uint256>();

                using (DBreeze.Transactions.Transaction transaction = blockRepo.DBreeze.GetTransaction())
                {
                    transaction.ValuesLazyLoadingIsOn = false;

                    byte[] hashBytes = uint256.Zero.ToBytes();

                    foreach (Row<byte[], byte[]> blockRow in transaction.SelectForward<byte[], byte[]>("Block"))
                    {
                        Array.Copy(blockRow.Value, sizeof(int), hashBytes, 0, hashBytes.Length);
                        uint256 hashPrev = serializer.Deserialize<uint256>(hashBytes);
                        var hashThis = new uint256(blockRow.Key);
                        prevBlock[hashThis] = hashPrev;
                    }
                }

                var nextBlock = prevBlock.ToDictionary(kv => kv.Value, kv => kv.Key);
                int firstHeight = 1;
                uint256 firstHash = nextBlock[this.network.GenesisHash];

                var chainTip = new ChainedHeader(new BlockHeader() { HashPrevBlock = this.network.GenesisHash }, firstHash, firstHeight);
                uint256 hash = firstHash;

                for (int height = firstHeight + 1; height <= blockRepo.TipHashAndHeight.Height; height++)
                {
                    hash = nextBlock[hash];
                    chainTip = new ChainedHeader(new BlockHeader() { HashPrevBlock = chainTip.HashBlock }, hash, chainTip);
                }

                // Build the chain indexer from the chain.
                var chainIndexer = new ChainIndexer(this.network, chainTip);

                // Load the JSON wallet. Bypasses IsExtPubKey wallet check.
                Wallet wallet = new FileStorage<Wallet>(nodeSettings.DataFolder.WalletPath).LoadByFileName($"{this.walletName}.wallet.json");

                // Initialize the repo.
                var repo = new SQLiteWalletRepository(dataFolder, this.network, DateTimeProvider.Default, new ScriptAddressReader(), new ScriptPubKeyProvider());
                repo.Initialize(this.dbPerWallet);

                // Create a new empty wallet in the repository.
                byte[] chainCode = wallet.ChainCode;
                repo.CreateWallet(this.walletName, wallet.EncryptedSeed, chainCode);

                // Verify the wallet exisits.
                Assert.Equal(this.walletName, repo.GetWalletNames().First());

                // Clone the JSON wallet accounts.
                foreach (HdAccount hdAccount in wallet.GetAccounts())
                {
                    var extPubKey = ExtPubKey.Parse(hdAccount.ExtendedPubKey);
                    repo.CreateAccount(this.walletName, hdAccount.Index, hdAccount.Name, extPubKey, "P2PKH");
                }

                // Process all the blocks in the repository.
                long ticksTotal = DateTime.Now.Ticks;
                long ticksReading = 0;

                IEnumerable<(ChainedHeader, Block)> TheSource()
                {
                    for (int height = firstHeight; height <= blockRepo.TipHashAndHeight.Height;)
                    {
                        var hashes = new List<uint256>();
                        for (int i = 0; i < 100 && (height + i) <= blockRepo.TipHashAndHeight.Height; i++)
                        {
                            ChainedHeader header = chainIndexer.GetHeader(height + i);
                            hashes.Add(header.HashBlock);
                        }

                        long flagFall = DateTime.Now.Ticks;

                        List<Block> blocks = blockRepo.GetBlocks(hashes);

                        ticksReading += (DateTime.Now.Ticks - flagFall);

                        var buffer = new List<(ChainedHeader, Block)>();
                        for (int i = 0; i < 100 && height <= blockRepo.TipHashAndHeight.Height; height++, i++)
                        {
                            ChainedHeader header = chainIndexer.GetHeader(height);
                            yield return ((header, blocks[i]));
                        }
                    }
                }

                repo.ProcessBlocks(TheSource(), this.walletName);

                // Calculate statistics. Set a breakpoint to inspect these values.
                ticksTotal = DateTime.Now.Ticks - ticksTotal;

                long secondsProcessing = (ticksTotal - ticksReading) / 10_000_000;

                double secondsPerBlock = (double)repo.ProcessTime / repo.ProcessCount / 10_000_000;

                // Now verify the DB against the JSON wallet.
                foreach (HdAccount hdAccount in wallet.GetAccounts())
                {
                    // Get the total balances.
                    (Money amountConfirmed, Money amountUnconfirmed) = hdAccount.GetBalances();

                    List<UnspentOutputReference> spendable = repo.GetSpendableTransactionsInAccount(
                        new WalletAccountReference(this.walletName, hdAccount.Name),
                        chainTip, (int)this.network.Consensus.CoinbaseMaturity).ToList();

                    Money balance = spendable.Sum(s => s.Transaction.Amount);

                    Assert.Equal(amountConfirmed, balance);
                }
            }
        }
    }
}