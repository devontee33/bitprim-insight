using bitprim.insight.DTOs;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Bitprim;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace bitprim.insight.Controllers
{
    /// <summary>
    /// Block related operations.
    /// </summary>
    [Route("[controller]")]
    [ApiController]
    public class BlockController : Controller
    {
        private readonly Chain chain_;
        private readonly IMemoryCache memoryCache_;
        private readonly NodeConfig config_;
        private readonly PoolsInfo poolsInfo_;

        /// <summary>
        /// Build this controller.
        /// </summary>
        /// <param name="config"> Higher level API configuration. </param>
        /// <param name="chain"> Executor's chain instance from bitprim-cs library. </param>
        /// <param name="memoryCache"> Abstract. </param>
        /// <param name="poolsInfo"> For recognizing blocks which come from mining pools. </param>
        public BlockController(IOptions<NodeConfig> config, Chain chain, IMemoryCache memoryCache, PoolsInfo poolsInfo)
        {
            config_ = config.Value;
            chain_ = chain;
            memoryCache_ = memoryCache;
            poolsInfo_ = poolsInfo;
        }

        /// <summary>
        /// Given a block hash, retrieve its univocally associated block.
        /// </summary>
        /// <param name="hash"> 32-character hex string. </param>
        /// <returns> The block with the given hash. </returns>
        [HttpGet("block/{hash}")]
        [ResponseCache(CacheProfileName = Constants.Cache.SHORT_CACHE_PROFILE_NAME)]
        [SwaggerOperation("GetBlockByHash")]
        [SwaggerResponse((int)System.Net.HttpStatusCode.OK, typeof(GetBlockByHashResponse))]
        [SwaggerResponse((int)System.Net.HttpStatusCode.BadRequest, typeof(string))]
        public async Task<ActionResult> GetBlockByHash(string hash)
        {
            if(!Validations.IsValidHash(hash))
            {
                return StatusCode((int)System.Net.HttpStatusCode.BadRequest, hash + " is not a valid block hash");
            }

            Utils.CheckIfChainIsFresh(chain_, config_.AcceptStaleRequests);

            if(memoryCache_.TryGetValue("block" + hash, out JsonResult cachedBlockJson))
            {
                return cachedBlockJson;
            };
            
            var binaryHash = Binary.HexStringToByteArray(hash);
            
            using(var getBlockResult = await chain_.FetchBlockHeaderByHashTxSizesAsync(binaryHash))
            {
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockHeaderByHashTxSizesAsync(" + hash + ") failed, check error log");
                
                var getLastHeightResult = await chain_.FetchLastHeightAsync();
                Utils.CheckBitprimApiErrorCode(getLastHeightResult.ErrorCode, "FetchLastHeightAsync() failed, check error log");
                
                var blockHeight = getBlockResult.Result.Block.BlockHeight;

                ApiCallResult<GetBlockHashTimestampResult> getNextBlockResult = null;
                if(blockHeight != getLastHeightResult.Result)
                {
                    getNextBlockResult = await chain_.FetchBlockByHeightHashTimestampAsync(blockHeight + 1);
                    Utils.CheckBitprimApiErrorCode(getNextBlockResult.ErrorCode, "FetchBlockByHeightHashTimestampAsync(" + blockHeight + 1 + ") failed, check error log");
                }
                
                decimal blockReward;
                PoolsInfo.PoolInfo poolInfo;
                using(DisposableApiCallResult<GetTxDataResult> coinbase = await chain_.FetchTransactionAsync(getBlockResult.Result.TransactionHashes[0], true))
                {
                    Utils.CheckBitprimApiErrorCode(coinbase.ErrorCode, "FetchTransactionAsync(" + getBlockResult.Result.TransactionHashes[0] + ") failed, check error log");
                    blockReward = Math.Round(Utils.SatoshisToCoinUnits(coinbase.Result.Tx.TotalOutputValue),1);
                    poolInfo = poolsInfo_.GetPoolInfo(coinbase.Result.Tx);
                }

                JsonResult blockJson = Json(BlockToJSON
                (
                    getBlockResult.Result.Block.BlockData, blockHeight, getBlockResult.Result.TransactionHashes,
                    blockReward, getLastHeightResult.Result, getNextBlockResult?.Result.BlockHash,
                    getBlockResult.Result.SerializedBlockSize, poolInfo)
                );

                memoryCache_.Set("block" + hash, blockJson, new MemoryCacheEntryOptions{Size = Constants.Cache.BLOCK_CACHE_ENTRY_SIZE});
                return blockJson;
            }
        }

        /// <summary>
        /// Given a block height, retrieve the block hash.
        /// </summary>
        /// <param name="height"> Block height. </param>
        /// <returns> Block hash as 32-character hex string. </returns>
        [HttpGet("block-index/{height}")]
        [ResponseCache(CacheProfileName = Constants.Cache.SHORT_CACHE_PROFILE_NAME)]
        [SwaggerOperation("GetBlockByHeight")]
        [SwaggerResponse((int)System.Net.HttpStatusCode.OK, typeof(GetBlockByHeightResponse))]
        public async Task<ActionResult> GetBlockByHeight(UInt64 height)
        {
            Utils.CheckIfChainIsFresh(chain_, config_.AcceptStaleRequests);
            
            var result = await chain_.FetchBlockByHeightHashTimestampAsync(height);
            Utils.CheckBitprimApiErrorCode(result.ErrorCode, "FetchBlockByHeightHashTimestampAsync(" + height + ") failed, error log");
            
            return Json
            (
                new GetBlockByHeightResponse
                {
                    blockHash = Binary.ByteArrayToHexString(result.Result.BlockHash)
                }
            );               
        }

        /// <summary>
        /// Given a date, return all blocks mined on that day.
        /// </summary>
        /// <param name="limit"> Max amount of blocks in result (older ones discarded). </param>
        /// <param name="blockDate"> Date to search, in the format specified in the settings. Defaults to yyyy-MM-dd (dashes required). </param>
        /// <returns> Block list. </returns>
        [HttpGet("blocks/")]
        [ResponseCache(CacheProfileName = Constants.Cache.SHORT_CACHE_PROFILE_NAME)]
        [SwaggerOperation("GetBlocksByDate")]
        [SwaggerResponse((int)System.Net.HttpStatusCode.OK, typeof(GetBlocksByDateResponse))]
        [SwaggerResponse((int)System.Net.HttpStatusCode.BadRequest, typeof(string))]
        public async Task<ActionResult> GetBlocksByDate(int limit = 200, string blockDate = "")
        {
            //Validate input
            var validateInputResult = ValidateGetBlocksByDateInput(limit, blockDate);
            if(!validateInputResult.Item1)
            {
                return StatusCode((int)System.Net.HttpStatusCode.BadRequest, validateInputResult.Item2);
            }
            
            var blockDateToSearch = validateInputResult.Item3;
            //These define the search interval (lte, gte)
            var gte = new DateTimeOffset(blockDateToSearch).ToUnixTimeSeconds();
            var lte =  gte + 86400;

            //Find blocks starting point
            Utils.CheckIfChainIsFresh(chain_, config_.AcceptStaleRequests);
            
            var getLastHeightResult = await chain_.FetchLastHeightAsync();
            Utils.CheckBitprimApiErrorCode(getLastHeightResult.ErrorCode, "FetchLastHeightAsync failed, check error log");
            
            var topHeight = getLastHeightResult.Result;
            var low = await FindFirstBlockFromNextDay(blockDateToSearch, topHeight);
            if(low == 0) //No blocks
            {
                return Json(BlocksByDateToJSON(new List<BlockSummary>(), blockDateToSearch, false, -1, lte));
            }
            
            //Grab the specified amount of blocks (limit)
            var startingHeight = low - 1;
            
            var blocks = await GetPreviousBlocks(startingHeight, (UInt64)limit, blockDateToSearch, topHeight);

            //Check if there are more blocks: grab one more earlier block
            var moreBlocks = await CheckIfMoreBlocks(startingHeight, (UInt64)limit, blockDateToSearch,lte);
            return Json(BlocksByDateToJSON(blocks, blockDateToSearch, moreBlocks.Item1, moreBlocks.Item2, lte));   
        }

        /// <summary>
        /// Given a block hash, return the block's representation as a hex string.
        /// </summary>
        /// <param name="hash"> 32-character hex string which univocally identifies the block in the blockchain. </param>
        /// <returns> Block raw data, as a hex string. </returns>
        [HttpGet("rawblock/{hash}")]
        [ResponseCache(CacheProfileName = Constants.Cache.LONG_CACHE_PROFILE_NAME)]
        [SwaggerOperation("GetRawBlockByHash")]
        [SwaggerResponse((int)System.Net.HttpStatusCode.OK, typeof(GetRawBlockResponse))]
        public async Task<ActionResult> GetRawBlockByHash(string hash)
        {
            Utils.CheckIfChainIsFresh(chain_, config_.AcceptStaleRequests);
            var binaryHash = Binary.HexStringToByteArray(hash);
            using(var getBlockResult = await chain_.FetchBlockByHashAsync(binaryHash))
            {
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHashAsync(" + hash + ") failed, check error log");
                var block = getBlockResult.Result.BlockData;
                return Json
                (
                    new GetRawBlockResponse
                    {
                        rawblock = Binary.ByteArrayToHexString(block.ToData(false).Reverse().ToArray())
                    }
                );
            }
        }

        /// <summary>
        /// Given a block height, return the block's representation as a hex string.
        /// </summary>
        /// <param name="height"> Height which univocally identifies the block in the blockchain. </param>
        /// <returns> Block raw data, as a hex string. </returns>
        [HttpGet("rawblock-index/{height}")]
        [ResponseCache(CacheProfileName = Constants.Cache.SHORT_CACHE_PROFILE_NAME)]
        [SwaggerOperation("GetRawBlockByHeight")]
        [SwaggerResponse((int)System.Net.HttpStatusCode.OK, typeof(GetRawBlockResponse))]
        public async Task<ActionResult> GetRawBlockByHeight(UInt64 height)
        {
            Utils.CheckIfChainIsFresh(chain_, config_.AcceptStaleRequests);
            using(var getBlockResult = await chain_.FetchBlockByHeightAsync(height))
            {
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHeightAsync(" + height + ") failed, check error log");
                var block = getBlockResult.Result.BlockData;
                return Json
                (
                    new
                    {
                        rawblock = Binary.ByteArrayToHexString(block.ToData(false).Reverse().ToArray())
                    }
                );
            }
        }

        private async Task<UInt64> FindFirstBlockFromNextDay(DateTime blockDateToSearch, UInt64 topHeight)
        {
            //Adapted binary search
            UInt64 low = 0;
            var high = topHeight;
            
            while(low < high)
            {
                var mid = (UInt64) ((double)low + (double) high)/2;
                
                var getBlockResult = await chain_.FetchBlockByHeightHashTimestampAsync(mid);
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHeightHashTimestampAsync(" + mid + ") failed, check error log");
                
                if(getBlockResult.Result.BlockTimestamp.Date <= blockDateToSearch.Date)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }
            //If the last block belongs to the sought date, the first block from the next day is "1 block off the chain"
            //We do this to avoid missing the latest block
            if(low == topHeight)
            {
                var getBlockResult = await chain_.FetchBlockByHeightHashTimestampAsync(topHeight);
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHeightHashTimestampAsync(" + topHeight + ") failed, check error log");
                if(getBlockResult.Result.BlockTimestamp.Date == blockDateToSearch.Date)
                {
                    low = topHeight + 1;
                }
            }
            return low;
        }

        private async Task<BlockSummary> GetBlockSummary(Block block, UInt64 height, UInt64 topHeight)
        {
            var hashStr = Binary.ByteArrayToHexString(block.Hash);
            var key = "blockSummary" + hashStr;

            if (memoryCache_.TryGetValue(key, out BlockSummary ret))
            {
                return ret;
            }

            using (var blockHeaderResult = await chain_.FetchBlockHeaderByHashTxSizesAsync(block.Hash))
            {
                Utils.CheckBitprimApiErrorCode(blockHeaderResult.ErrorCode,
                    "FetchBlockHeaderByHashTxSizesAsync(" + block.Hash + ") failed, check error log");

                PoolsInfo.PoolInfo poolInfo;
                using(DisposableApiCallResult<GetTxDataResult> coinbase = await chain_.FetchTransactionAsync(blockHeaderResult.Result.TransactionHashes[0], true))
                {
                    Utils.CheckBitprimApiErrorCode(coinbase.ErrorCode, "FetchTransactionAsync(" + blockHeaderResult.Result.TransactionHashes[0] + ") failed, check error log");
                    poolInfo = poolsInfo_.GetPoolInfo(coinbase.Result.Tx);
                }
                
                var blockSummary = new BlockSummary
                {
                    height = height,
                    size = block.GetSerializedSize(block.Header.Version),
                    hash = Binary.ByteArrayToHexString(block.Hash),
                    time = block.Header.Timestamp,
                    txlength = block.TransactionCount,
                    poolInfo = new PoolInfo{ poolName = poolInfo.Name, url = poolInfo.Url}
                };

                var confirmations = topHeight - height + 1;
                if (confirmations >= Constants.Cache.BLOCK_CACHE_CONFIRMATIONS)
                {
                    memoryCache_.Set(key, blockSummary, new MemoryCacheEntryOptions{Size = Constants.Cache.BLOCK_CACHE_SUMMARY_SIZE});
                }

                return blockSummary;
            }
        }

        private async Task<List<BlockSummary>> GetPreviousBlocks(UInt64 startingHeight, UInt64 blockCount, DateTime blockDateToSearch, UInt64 topHeight)
        {
            var blocks = new List<BlockSummary>();
            var blockWithinDate = true;
            
            for(UInt64 i=0; i<blockCount && startingHeight>=i && blockWithinDate; i++)
            {
                using(var getBlockResult = await chain_.FetchBlockByHeightAsync(startingHeight - i))
                {
                    Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHeightAsync(" + (startingHeight - i) + ") failed, check error log");
                    
                    var block = getBlockResult.Result.BlockData;

                    blockWithinDate = DateTimeOffset.FromUnixTimeSeconds(block.Header.Timestamp).UtcDateTime.Date == blockDateToSearch.Date;
                    
                    if(blockWithinDate)
                    {
                        blocks.Add(await GetBlockSummary(block,getBlockResult.Result.BlockHeight, topHeight)); 
                    }
                }
            }
            return blocks;
        }

        private async Task<Tuple<bool, long>> CheckIfMoreBlocks(UInt64 startingHeight, UInt64 limit, DateTime blockDateToSearch, long lte)
        {
            bool moreBlocks;
            long moreBlocksTs = lte;
            
            if(startingHeight <= limit)
            {
                moreBlocks = false;
            }
            else
            {
                var getBlockResult = await chain_.FetchBlockByHeightHashTimestampAsync(startingHeight - limit);
                Utils.CheckBitprimApiErrorCode(getBlockResult.ErrorCode, "FetchBlockByHeightHashTimestampAsync(" + (startingHeight - limit) + ") failed, check error log");
                
                var blockDateUtc = getBlockResult.Result.BlockTimestamp;
                
                moreBlocks = blockDateUtc.Date == blockDateToSearch.Date;
            }
            return new Tuple<bool, long>(moreBlocks, moreBlocksTs);
        }

        private object BlocksByDateToJSON(List<BlockSummary> blocks, DateTime blockDateToSearch, bool moreBlocks, long moreBlocksTs, long lte)
        {
            return new GetBlocksByDateResponse
            {
                blocks = blocks.ToArray(),
                length = blocks.Count,
                pagination = new Pagination
                {
                    next = blockDateToSearch.Date.AddDays(+1).ToString(config_.DateInputFormat),
                    prev = blockDateToSearch.Date.AddDays(-1).ToString(config_.DateInputFormat),
                    currentTs = lte - 1,
                    current = blockDateToSearch.Date.ToString(config_.DateInputFormat),
                    isToday = blockDateToSearch.Date == DateTime.UtcNow.Date,
                    more = moreBlocks,
                    moreTs = moreBlocks ? moreBlocksTs : (long?)null
                }
            };
        }

        private static GetBlockByHashResponse BlockToJSON(Header blockHeader, UInt64 blockHeight, HashList txHashes,
                                                          decimal blockReward, UInt64 currentHeight, byte[] nextBlockHash,
                                                        UInt64 serializedBlockSize, PoolsInfo.PoolInfo poolInfo)
        {
            BigInteger.TryParse(blockHeader.ProofString, out var proof);
            var blockJson = new GetBlockByHashResponse();
            blockJson.hash = Binary.ByteArrayToHexString(blockHeader.Hash);
            blockJson.size = serializedBlockSize;
            blockJson.height = blockHeight;
            blockJson.version = blockHeader.Version;
            blockJson.merkleroot = Binary.ByteArrayToHexString(blockHeader.Merkle);
            blockJson.tx = BlockTxsToJSON(txHashes);
            blockJson.time = blockHeader.Timestamp;
            blockJson.nonce = blockHeader.Nonce;
            blockJson.bits = Utils.EncodeInBase16(blockHeader.Bits);
            blockJson.difficulty = Utils.BitsToDifficulty(blockHeader.Bits); //TODO Use bitprim API when implemented
            blockJson.chainwork = (proof * 2).ToString("X64"); //TODO Does not match Blockdozer value; check how bitpay calculates it
            blockJson.confirmations = currentHeight - blockHeight + 1;
            blockJson.previousblockhash = Binary.ByteArrayToHexString(blockHeader.PreviousBlockHash);
            if(nextBlockHash != null)
            {
                blockJson.nextblockhash = Binary.ByteArrayToHexString(nextBlockHash);
            }
            blockJson.reward = blockReward;
            blockJson.isMainChain = true; //TODO Check value
            blockJson.poolInfo = new PoolInfo{ poolName = poolInfo.Name, url = poolInfo.Url};
            return blockJson;
        }

        private static string[] BlockTxsToJSON(HashList txHashes)
        {
            var txs = new List<string>();
            for(uint i = 0; i<txHashes.Count; i++)
            {
                txs.Add(Binary.ByteArrayToHexString(txHashes[i]));
            }
            return txs.ToArray();
        }

        private Tuple<bool, string, DateTime> ValidateGetBlocksByDateInput(int limit, string blockDate)
        {
            if(limit <= 0)
            {
                return new Tuple<bool, string, DateTime>(false, "Invalid limit; must be greater than zero", DateTime.MinValue);
            }

            if(limit > config_.MaxBlockSummarySize)
            {
                return new Tuple<bool, string, DateTime>(false, "Invalid limit; must be lower than " + config_.MaxBlockSummarySize, DateTime.MinValue);
            }

            if(string.IsNullOrWhiteSpace(blockDate))
            {
                blockDate = DateTime.Today.ToString(config_.DateInputFormat);
            }

            if(!DateTime.TryParseExact(blockDate, config_.DateInputFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var blockDateToSearch))
            {
                return new Tuple<bool, string, DateTime>(false, "Invalid date format; expected " + config_.DateInputFormat, DateTime.MinValue);
            }

            return new Tuple<bool, string, DateTime>(true, "", blockDateToSearch);
        }
    }
}
