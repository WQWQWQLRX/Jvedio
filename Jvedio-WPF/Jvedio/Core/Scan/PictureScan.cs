﻿using Jvedio.Core.CustomEventArgs;
using Jvedio.Core.Enums;
using Jvedio.Core.Logs;
using Jvedio.Entity;
using Jvedio.Entity.Data;
using Jvedio.Mapper;
using JvedioLib.Security;
using SuperUtils.CustomEventArgs;
using SuperUtils.IO;
using SuperUtils.Time;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static Jvedio.MapperManager;

namespace Jvedio.Core.Scan
{
    public class PictureScan : ScanTask
    {
        public Dictionary<string, List<string>> pathDict;

        public Enums.DataType dataType = Enums.DataType.Picture;

        public PictureScan(List<string> scanPaths, List<string> filePaths, IEnumerable<string> fileExt = null, Enums.DataType dataType = DataType.Picture) : base(scanPaths, filePaths, fileExt)
        {
            pathDict = new Dictionary<string, List<string>>();
            this.dataType = dataType;
        }

        private (List<Picture>, List<string>) parsePicture()
        {
            // 仅支持根目录
            List<Picture> import = new List<Picture>();
            List<string> notImport = new List<string>();

            if (pathDict == null || pathDict.Keys.Count == 0) return (null, null);

            foreach (string path in pathDict.Keys)
            {
                List<string> list = pathDict[path];

                // 过滤后缀
                List<string> videoPaths = list.Where(arg => VIDEO_EXTENSIONS_LIST.Contains(Path.GetExtension(arg).ToLower())).ToList();
                List<string> imgPaths = list.Where(arg => FileExt.Contains(Path.GetExtension(arg).ToLower())).ToList();

                Picture picture = new Picture();
                picture.DataType = dataType;
                picture.Title = Path.GetFileName(path);
                picture.PicCount = imgPaths.Count;
                picture.VideoPaths = string.Join(SuperUtils.Values.ConstValues.SeparatorString, videoPaths);
                picture.PicPaths = string.Join(SuperUtils.Values.ConstValues.SeparatorString, imgPaths.Select(arg => Path.GetFileName(arg)));
                picture.Path = path;
                long totalSize = 0;
                foreach (string imgPath in imgPaths)
                {
                    totalSize += new FileInfo(imgPath).Length;
                }

                picture.Size = totalSize;

                // 计算哈希
                imgPaths.AddRange(videoPaths);
                notImport.AddRange(list.Except(imgPaths));
                picture.Hash = Encrypt.FasterDirMD5(imgPaths);
                import.Add(picture);
            }

            return (import, notImport);
        }

        public override void DoWork()
        {
            Task.Run(() =>
            {
                stopwatch.Start();
                foreach (string path in ScanPaths)
                {
                    List<string> list = FileHelper.TryGetAllFiles(path, "*.*").ToList();
                    if (list != null && list.Count > 0)
                        pathDict.Add(path, list);
                }

                try
                {
                    CheckStatus();
                }
                catch (TaskCanceledException ex)
                {
                    Console.WriteLine(ex.Message);
                    return;
                }

                ScanHelper scanHelper = new ScanHelper();

                (List<Picture> import, List<string> notImport) = parsePicture();

                try
                {
                    CheckStatus();
                }
                catch (TaskCanceledException ex)
                {
                    Console.WriteLine(ex.Message);
                    return;
                }

                handleImport(import);
                handleNotImport(notImport);

                stopwatch.Stop();
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                ScanResult.ElapsedMilliseconds = ElapsedMilliseconds;
                Status = System.Threading.Tasks.TaskStatus.RanToCompletion;
            });
        }

        private void handleImport(List<Picture> import)
        {
            string sql = PictureMapper.BASE_SQL;
            sql = "select metadata.DataID,Hash,Size,Path,PID " + sql;
            List<Dictionary<string, object>> list = pictureMapper.Select(sql);
            List<Picture> existPictures = pictureMapper.ToEntity<Picture>(list, typeof(Picture).GetProperties(), false);

            // 1.1 不需要导入
            // 存在同路径、同哈希的图片路径
            foreach (var item in import.Where(arg => existPictures.Where(t => arg.Path.Equals(t.Path) && arg.Hash.Equals(t.Hash)).Any()))
            {
                ScanResult.NotImport.Add(item.Path, new ScanDetailInfo("同路径、同哈希"));
            }

            import.RemoveAll(arg => existPictures.Where(t => arg.Path.Equals(t.Path) && arg.Hash.Equals(t.Hash)).Any());

            // 1.2 需要 update
            // 哈希相同，路径不同
            List<Picture> toUpdate = new List<Picture>();
            foreach (Picture data in import)
            {
                Picture existData = existPictures.Where(t => data.Hash.Equals(t.Hash) && !data.Path.Equals(t.Path)).FirstOrDefault();
                if (existData != null)
                {
                    data.DataID = existData.DataID;
                    data.PID = existData.PID;
                    data.LastScanDate = DateHelper.Now();
                    toUpdate.Add(data);
                    ScanResult.Update.Add(data.Path, "哈希相同，路径不同");
                }
            }

            import.RemoveAll(arg => existPictures.Where(t => arg.Hash.Equals(t.Hash) && !arg.Path.Equals(t.Path)).Any());

            // 1.3 需要 update
            // 哈希不同，路径相同
            foreach (Picture data in import)
            {
                Picture existData = existPictures.Where(t => data.Path.Equals(t.Path) && !data.Hash.Equals(t.Hash)).FirstOrDefault();
                if (existData != null)
                {
                    data.DataID = existData.DataID;
                    data.PID = existData.PID;
                    data.LastScanDate = DateHelper.Now();
                    toUpdate.Add(data);
                    ScanResult.Update.Add(data.Path, "哈希不同，路径相同");
                }
            }

            import.RemoveAll(arg => existPictures.Where(t => arg.Path.Equals(t.Path) && !arg.Hash.Equals(t.Hash)).Any());

            // 1.3 需要 insert
            List<Picture> toInsert = import;

            // 1.更新
            List<MetaData> toUpdateData = toUpdate.Select(arg => arg.toMetaData()).ToList();

            metaDataMapper.UpdateBatch(toUpdateData, "Title", "Size", "Hash", "Path", "LastScanDate");
            if (dataType == DataType.Picture)
                pictureMapper.UpdateBatch(toUpdate, "PicCount", "PicPaths", "VideoPaths");
            else if (dataType == DataType.Comics)
                comicMapper.UpdateBatch(toUpdate.Select(arg => arg.toSimpleComic()).ToList(), "PicCount", "PicPaths");

            // 2.导入
            foreach (Picture data in toInsert)
            {
                data.DBId = ConfigManager.Main.CurrentDBId;
                data.FirstScanDate = DateHelper.Now();
                data.LastScanDate = DateHelper.Now();
                ScanResult.Import.Add(data.Path);
            }

            List<MetaData> toInsertData = toInsert.Select(arg => arg.toMetaData()).ToList();
            if (toInsertData.Count <= 0) return;
            long.TryParse(metaDataMapper.InsertAndGetID(toInsertData[0]).ToString(), out long before);
            toInsertData.RemoveAt(0);
            try
            {
                metaDataMapper.ExecuteNonQuery("BEGIN TRANSACTION;"); // 开启事务，这样子其他线程就不能更新
                metaDataMapper.InsertBatch(toInsertData);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                OnError(new MessageCallBackEventArgs(ex.Message));
            }
            finally
            {
                metaDataMapper.ExecuteNonQuery("END TRANSACTION;");
            }

            // 处理 DataID
            foreach (Picture data in toInsert)
            {
                data.DataID = before;
                before++;
            }

            try
            {
                if (dataType == DataType.Picture)
                {
                    pictureMapper.ExecuteNonQuery("BEGIN TRANSACTION;"); // 开启事务，这样子其他线程就不能更新
                    pictureMapper.InsertBatch(toInsert);
                }
                else if (dataType == DataType.Comics)
                {
                    comicMapper.ExecuteNonQuery("BEGIN TRANSACTION;"); // 开启事务，这样子其他线程就不能更新
                    comicMapper.InsertBatch(toInsert.Select(arg => arg.toSimpleComic()).ToList());
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                OnError(new MessageCallBackEventArgs(ex.Message));
            }
            finally
            {
                if (dataType == DataType.Picture)
                    pictureMapper.ExecuteNonQuery("END TRANSACTION;");
                else if (dataType == DataType.Comics)
                    comicMapper.ExecuteNonQuery("END TRANSACTION;");
            }
        }

        private void handleNotImport(List<string> notImport)
        {
            foreach (string path in notImport)
            {
                ScanResult.NotImport.Add(path, new ScanDetailInfo("不导入"));
            }
        }
    }
}
