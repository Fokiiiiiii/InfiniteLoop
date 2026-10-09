using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.bigworld.common.course;
using AscNet.Table.V2.share.bigworld.common.message;
using AscNet.Table.V2.share.bigworld.common.photograph;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateBigWorldArchive()
        {
            Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldArchiveModule");
            object? Call(string name, params object[] args)
            {
                try { return module.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException!; }
            }

            const long playerId = 99_811;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "big-world-archive");
            int packetId = 0;
            TResp Req<TResp>(string name, object? request, int maxPackets = 1)
            {
                InvokeRegisteredRequestHandler(name, harness.Session, ++packetId, request);
                return ReadResponsePayload<TResp>(harness, packetId, name.Replace("Request", "Response"), name, maxPackets);
            }
            void NoPacket(string name)
            {
                if (harness.TryReadAvailablePacket(name, out Packet extra))
                    throw new InvalidDataException($"{name}: unexpected {extra.Type} packet.");
            }
            static string J(IEnumerable<int> ids) => string.Join(",", ids);
            Player Reload() => BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);

            // ---- Album ----
            AssertEqual(0, Req<BigWorldAlbumDataResponse>("BigWorldAlbumDataRequest", null).PhotoDatas.Count, "album empty");
            BigWorldAlbumAddPhotoResponse free = Req<BigWorldAlbumAddPhotoResponse>("BigWorldAlbumAddPhotoRequest", new BigWorldAlbumAddPhotoRequest());
            AssertEqual((0, 1, false, 0), (free.Code, free.PhotoData!.Id, free.PhotoData.IsTaskHidePhoto, free.PhotoData.MessageRefId), "album add free");
            BigWorldAlbumAddPhotoResponse task = Req<BigWorldAlbumAddPhotoResponse>("BigWorldAlbumAddPhotoRequest", new BigWorldAlbumAddPhotoRequest { IsTaskHidePhoto = true, TaskHideUniqueId = 5 });
            AssertEqual((2, true, 5), (task.PhotoData!.Id, task.PhotoData.IsTaskHidePhoto, task.PhotoData.MessageRefId), "album add task photo");
            BigWorldAlbumAddPhotoResponse reshot = Req<BigWorldAlbumAddPhotoResponse>("BigWorldAlbumAddPhotoRequest", new BigWorldAlbumAddPhotoRequest { IsTaskHidePhoto = true, TaskHideUniqueId = 5 });
            BigWorldAlbumDataResponse album = Req<BigWorldAlbumDataResponse>("BigWorldAlbumDataRequest", null);
            AssertEqual((1, 1, reshot.PhotoData!.Id), (album.PhotoDatas.Count, album.TaskPhotoData.Count, album.TaskPhotoData.Keys.Single()), "album task reshot replaces");
            AssertEqual(free.PhotoData.CheckSalt, album.PhotoDatas[0].CheckSalt, "album salt stable");

            AssertEqual(0, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 1, Remark = "beach" }).Code, "remark ok");
            AssertEqual(25200099, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 77, Remark = "x" }).Code, "remark photo missing");
            AssertEqual(25200096, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 1, Remark = " " }).Code, "remark empty");
            AssertEqual(0, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 1, Remark = new string('a', 12) }).Code, "remark at limit 12");
            AssertEqual(25200098, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 1, Remark = new string('a', 13) }).Code, "remark too long");
            AssertEqual(25200097, Req<BigWorldAlbumUpdatePhotoResponse>("BigWorldAlbumUpdatePhotoRequest", new BigWorldAlbumUpdatePhotoRequest { PhotoId = 1, Remark = "a\nb" }).Code, "remark invalid");
            AssertEqual(new string('a', 12), Reload().BigWorldState.AlbumPhotos.Single(p => p.Id == 1).Remark, "remark persisted");
            AssertEqual(25200095, Req<BigWorldAlbumDeletePhotoResponse>("BigWorldAlbumDeletePhotoRequest", new BigWorldAlbumDeletePhotoRequest { PhotoIds = [] }).Code, "delete empty");
            AssertEqual(25200095, Req<BigWorldAlbumDeletePhotoResponse>("BigWorldAlbumDeletePhotoRequest", new BigWorldAlbumDeletePhotoRequest { PhotoIds = [1, 99] }).Code, "delete unknown id");
            AssertEqual(0, Req<BigWorldAlbumDeletePhotoResponse>("BigWorldAlbumDeletePhotoRequest", new BigWorldAlbumDeletePhotoRequest { PhotoIds = [1] }).Code, "delete ok");
            AssertEqual(false, Reload().BigWorldState.AlbumPhotos.Any(p => p.Id == 1), "delete persisted");
            for (int i = 0; i < 100; i++)
                player.BigWorldState.AlbumPhotos.Add(new BigWorldAlbumPhoto { Id = 1000 + i });
            AssertEqual(25200094, Req<BigWorldAlbumAddPhotoResponse>("BigWorldAlbumAddPhotoRequest", new BigWorldAlbumAddPhotoRequest()).Code, "album full at capacity 100");
            AssertEqual(0, Req<BigWorldAlbumAddPhotoResponse>("BigWorldAlbumAddPhotoRequest", new BigWorldAlbumAddPhotoRequest { IsTaskHidePhoto = true, TaskHideUniqueId = 6 }).Code, "hidden photos bypass capacity");
            player.BigWorldState.AlbumPhotos.RemoveAll(p => p.Id >= 1000);

            // ---- Photograph unlocks from reward goods ----
            int filterId = TableReaderV2.Parse<BigWorldPhotographFiltersTable>().First(row => row.ObtainType == 0).Id;
            const int actionId = 31000102; // granted by retail goods 70002100
            Call("ApplySliceRewards", harness.Session, new List<RewardGoods> { new() { TemplateId = filterId, Count = 1 }, new() { TemplateId = actionId, Count = 1 } });
            NotifyBigWorldPhotographDataUpdate photo = ReadPushPayload<NotifyBigWorldPhotographDataUpdate>(harness, nameof(NotifyBigWorldPhotographDataUpdate), "photograph push");
            AssertEqual((filterId, actionId), (photo.BigWorldPhotographData.UnlockedCameraFilters.Single(), photo.BigWorldPhotographData.UnlockedCharacterActions.Single()), "photograph unlock");
            Call("ApplySliceRewards", harness.Session, new List<RewardGoods> { new() { TemplateId = filterId, Count = 1 } });
            NoPacket("photograph repeat grant");
            AssertEqual(filterId, Reload().BigWorldState.UnlockedCameraFilters.Single(), "photograph persisted");

            // ---- Course ----
            AssertEqual((0, J(new[] { 1001 })), Req<BigWorldCourseCoreSetReadResponse>("BigWorldCourseCoreSetReadRequest", new BigWorldCourseCoreSetReadRequest { VersionId = 1, ElementIds = [1001, 9999] }) is var r1 ? (r1.Code, J(r1.SuccessIds)) : default, "core read partial");
            AssertEqual(20290017, Req<BigWorldCourseCoreSetReadResponse>("BigWorldCourseCoreSetReadRequest", new BigWorldCourseCoreSetReadRequest { VersionId = 1, ElementIds = [1001] }).Code, "core already read");
            AssertEqual(20290016, Req<BigWorldCourseCoreSetReadResponse>("BigWorldCourseCoreSetReadRequest", new BigWorldCourseCoreSetReadRequest { VersionId = 1, ElementIds = [2001] }).Code, "core element of another content");
            AssertEqual(20290001, Req<BigWorldCourseCoreSetReadResponse>("BigWorldCourseCoreSetReadRequest", new BigWorldCourseCoreSetReadRequest { VersionId = 99 }).Code, "core unknown version");
            AssertEqual(20290001, Req<BigWorldCourseCoreSetReadResponse>("BigWorldCourseCoreSetReadRequest", new BigWorldCourseCoreSetReadRequest { VersionId = 2, ElementIds = [2101] }).Code, "core version 2 condition closed");
            AssertEqual(J(new[] { 1001 }), J(Reload().BigWorldState.CourseReadElementIds[1]), "core read persisted per version");

            AssertEqual(20290005, Req<BigWorldCourseTaskCntGetRewardResponse>("BigWorldCourseTaskCntGetRewardRequest", new BigWorldCourseTaskCntGetRewardRequest { VersionId = 1 }).Code, "task reward without medals");
            List<BigWorldCourseTaskProgressRewardTable> v1Rewards = TableReaderV2.Parse<BigWorldCourseTaskProgressRewardTable>().Where(row => row.ContentId == 101).OrderBy(row => row.Progress).ToList();
            inventory.Items.Add(new Item { Id = 950000, Count = v1Rewards[1].Progress });
            BigWorldCourseTaskCntGetRewardResponse taskClaim = Req<BigWorldCourseTaskCntGetRewardResponse>("BigWorldCourseTaskCntGetRewardRequest", new BigWorldCourseTaskCntGetRewardRequest { VersionId = 1 }, 32);
            AssertEqual(J(v1Rewards.Where(row => row.Progress <= v1Rewards[1].Progress).Select(row => row.Id)), J(taskClaim.GotRewardIds), "task claims every reached tier");
            AssertEqual(true, taskClaim.RewardGoodsList.Count > 0, "task reward goods");
            AssertEqual(20290005, Req<BigWorldCourseTaskCntGetRewardResponse>("BigWorldCourseTaskCntGetRewardRequest", new BigWorldCourseTaskCntGetRewardRequest { VersionId = 1 }).Code, "task reward once");
            AssertEqual(20290001, Req<BigWorldCourseTaskCntGetRewardResponse>("BigWorldCourseTaskCntGetRewardRequest", new BigWorldCourseTaskCntGetRewardRequest { VersionId = 42 }).Code, "task unknown version");
            Call("ApplySliceRewards", harness.Session, new List<RewardGoods> { new() { TemplateId = 950000, Count = 1 } });
            AssertEqual(v1Rewards[1].Progress, ReadPushPayload<NotifyBigWorldCourseTaskCntProgress>(harness, nameof(NotifyBigWorldCourseTaskCntProgress), "task progress push").TotalProgress, "task progress = medal count");

            AssertEqual(20290003, Req<BigWorldCourseExploreCntGetRewardResponse>("BigWorldCourseExploreCntGetRewardRequest", new BigWorldCourseExploreCntGetRewardRequest { ExploreId = 777 }).Code, "explore unknown");
            AssertEqual(20290014, Req<BigWorldCourseExploreCntGetRewardResponse>("BigWorldCourseExploreCntGetRewardRequest", new BigWorldCourseExploreCntGetRewardRequest { ExploreId = 1 }).Code, "explore not enough");
            AssertEqual(true, (bool)Call("ReportExplorePoi", harness.Session, 101, 4)!, "report 101=4");
            NotifyBigWorldCourseExploreProgress progress = ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "explore push");
            AssertEqual((1, 1, 101, 4), (progress.VersionId, progress.ExploreId, progress.PoiId, progress.Count), "explore push matches pcap oracle");
            AssertEqual(false, (bool)Call("ReportExplorePoi", harness.Session, 101, 3)!, "report lower count ignored");
            NoPacket("explore lower count");
            Call("ReportExplorePoi", harness.Session, 101, 99);
            AssertEqual(6, ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "clamp push").Count, "report clamped to TotalProgress");
            AssertThrows<InvalidDataException>(() => Call("ReportExplorePoi", harness.Session, 5555, 1), "report unknown poi");
            Call("ReportExplorePoi", harness.Session, 102, 7);
            Call("ReportExplorePoi", harness.Session, 103, 6);
            ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "poi 102 push");
            ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "poi 103 push");
            AssertEqual(20290009, Req<BigWorldCourseExploreCntGetCompleteRewardResponse>("BigWorldCourseExploreCntGetCompleteRewardRequest", new BigWorldCourseExploreCntGetCompleteRewardRequest { VersionId = 1 }).Code, "complete not ready");
            BigWorldCourseExploreCntGetRewardResponse exploreClaim = Req<BigWorldCourseExploreCntGetRewardResponse>("BigWorldCourseExploreCntGetRewardRequest", new BigWorldCourseExploreCntGetRewardRequest { ExploreId = 1 }, 32);
            AssertEqual(0, exploreClaim.Code, "explore reward ok");
            AssertEqual(20290011, Req<BigWorldCourseExploreCntGetRewardResponse>("BigWorldCourseExploreCntGetRewardRequest", new BigWorldCourseExploreCntGetRewardRequest { ExploreId = 1 }).Code, "explore reward once");
            Call("ReportExplorePoi", harness.Session, 201, 3);
            Call("ReportExplorePoi", harness.Session, 202, 3);
            ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "poi 201 push");
            ReadPushPayload<NotifyBigWorldCourseExploreProgress>(harness, nameof(NotifyBigWorldCourseExploreProgress), "poi 202 push");
            AssertEqual(0, Req<BigWorldCourseExploreCntGetCompleteRewardResponse>("BigWorldCourseExploreCntGetCompleteRewardRequest", new BigWorldCourseExploreCntGetCompleteRewardRequest { VersionId = 1 }, 32).Code, "complete reward ok");
            AssertEqual(20290008, Req<BigWorldCourseExploreCntGetCompleteRewardResponse>("BigWorldCourseExploreCntGetCompleteRewardRequest", new BigWorldCourseExploreCntGetCompleteRewardRequest { VersionId = 1 }).Code, "complete reward once");

            BigWorldPlayerState reloaded = Reload().BigWorldState;
            AssertEqual((6, true, true), (reloaded.CoursePoiCounts[101], reloaded.CourseExploreRewardIds.Contains(1), reloaded.CourseExploreCompleteVersionIds.Contains(1)), "course persisted");

            Call("SendEnterPushes", harness.Session);
            NotifyBigWorldAlbumUpdate enterAlbum = ReadPushPayload<NotifyBigWorldAlbumUpdate>(harness, nameof(NotifyBigWorldAlbumUpdate), "enter album");
            AssertEqual((player.BigWorldState.AlbumPhotoIdSequence, 0, 2), (enterAlbum.AlbumData.PhotoIdSequence, enterAlbum.AlbumData.PhotoDatas.Count, enterAlbum.AlbumData.TaskHidePhotoData.Count), "enter album from state");
            NotifyBigWorldCourseData enterCourse = ReadPushPayload<NotifyBigWorldCourseData>(harness, nameof(NotifyBigWorldCourseData), "enter course");
            BigWorldCourseVersionData v1 = enterCourse.Data.Datas[1];
            AssertEqual((101, 102, 103, true, true, 6), (v1.TaskCntData.ContentId, v1.ExploreCntData.ContentId, v1.CoreCntData.ContentId, v1.ExploreCntData.IsGotCompleteReward, v1.ExploreCntData.ExploreDatas[1].IsGotReward, v1.ExploreCntData.ExploreDatas[1].PoiCounts[101]), "enter course v1");
            AssertEqual((3, 202, 0), (enterCourse.Data.Datas.Count, enterCourse.Data.Datas[2].ExploreCntData.ContentId, enterCourse.Data.Datas[2].TaskCntData.GotRewardIds.Count), "enter course versions from table");

            // ---- Message ----
            BigWorldMessageTable message = TableReaderV2.Parse<BigWorldMessageTable>().Single(row => row.Id == 1001);
            Dictionary<int, BigWorldMessageStepTable> steps = TableReaderV2.Parse<BigWorldMessageStepTable>().ToDictionary(row => row.Id);
            AssertEqual(25200019, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = message.FirstStepId }).Code, "message not active");
            AssertEqual(0, (int)Call("ActivateMessage", harness.Session, 1001)!, "activate");
            NotifyBigWorldNotReadMessage unread = ReadPushPayload<NotifyBigWorldNotReadMessage>(harness, nameof(NotifyBigWorldNotReadMessage), "unread push");
            AssertEqual((1001, message.FirstStepId, 2), (unread.MessageId, unread.StepId, unread.State), "unread push");
            AssertEqual(25200018, (int)Call("ActivateMessage", harness.Session, 1001)!, "activate twice");
            AssertEqual(0, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = message.FirstStepId }).Code, "read first");
            AssertEqual(25200011, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = message.FirstStepId }).Code, "read repeat");
            AssertEqual(25200009, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = -1 }).Code, "read unknown step");
            int foreign = TableReaderV2.Parse<BigWorldMessageTable>().Single(row => row.Id == 1002).FirstStepId;
            AssertEqual(25200009, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = foreign }).Code, "read unreachable step");
            int terminal = message.FirstStepId;
            while (steps[terminal].NextStep.Any(id => id > 0))
                terminal = steps[terminal].NextStep.First(id => id > 0);
            AssertEqual(0, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = terminal }).Code, "read terminal");
            AssertEqual(25200012, Req<BigWorldMessageReadRecordResponse>("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 1001, StepId = terminal }).Code, "read after finish");
            BigWorldMessageRecord savedMessage = Reload().BigWorldState.Messages[1001];
            AssertEqual((1, J(new[] { message.FirstStepId, terminal })), (savedMessage.State, J(savedMessage.StepIds)), "message persisted");
            Call("SendMessageRecordUpdate", harness.Session);
            AssertEqual(terminal, ReadPushPayload<NotifyBigWorldMessageRecordUpdate>(harness, nameof(NotifyBigWorldMessageRecordUpdate), "record push").BigWorldMessageDict[1001].StepRecordList[^1].LatestStepId, "record push");

            // ---- News ----
            BigWorldNewsMarkPopupResponse news = Req<BigWorldNewsMarkPopupResponse>("BigWorldNewsMarkPopupRequest", new BigWorldNewsMarkPopupRequest { NewsIds = [1, 999999, 2] });
            AssertEqual((0, J(new[] { 1, 2 })), (news.Code, J(news.MarkedNewsIds)), "news marks eligible subset");
            AssertEqual(25200117, Req<BigWorldNewsMarkPopupResponse>("BigWorldNewsMarkPopupRequest", new BigWorldNewsMarkPopupRequest { NewsIds = [1] }).Code, "news already popped");
            AssertEqual(25200116, Req<BigWorldNewsMarkPopupResponse>("BigWorldNewsMarkPopupRequest", new BigWorldNewsMarkPopupRequest { NewsIds = [999999] }).Code, "news unknown");
            AssertEqual(25200117, Req<BigWorldNewsMarkPopupResponse>("BigWorldNewsMarkPopupRequest", new BigWorldNewsMarkPopupRequest { NewsIds = [] }).Code, "news empty");
            AssertEqual(J(new[] { 1, 2 }), J(Reload().BigWorldState.NewsPopupIds), "news persisted");

            // ---- Help course ----
            AssertEqual(25200013, Req<BigWorldHelpCourseUnlockResponse>("BigWorldHelpCourseUnlockRequest", new BigWorldHelpCourseUnlockRequest { CourseId = 4 }).Code, "help unknown");
            AssertEqual(25200015, Req<BigWorldHelpCourseReadResponse>("BigWorldHelpCourseReadRequest", new BigWorldHelpCourseReadRequest { CourseId = 1010 }).Code, "help read locked");
            InvokeRegisteredRequestHandler("BigWorldHelpCourseUnlockRequest", harness.Session, ++packetId, new BigWorldHelpCourseUnlockRequest { CourseId = 1010 });
            AssertEqual(0, ReadResponsePayload<BigWorldHelpCourseUnlockResponse>(harness, packetId, "BigWorldHelpCourseUnlockResponse", "help unlock").Code, "help unlock");
            NotifyBigWorldHelpCourseUnlock unlock = ReadPushPayload<NotifyBigWorldHelpCourseUnlock>(harness, nameof(NotifyBigWorldHelpCourseUnlock), "help push");
            AssertEqual((1010, false), (unlock.Data.Id, unlock.Data.IsRead), "help push");
            InvokeRegisteredRequestHandler("BigWorldHelpCourseUnlockRequest", harness.Session, ++packetId, new BigWorldHelpCourseUnlockRequest { CourseId = 2001, IsRead = true });
            ReadResponsePayload<BigWorldHelpCourseUnlockResponse>(harness, packetId, "BigWorldHelpCourseUnlockResponse", "help unlock read");
            AssertEqual(true, ReadPushPayload<NotifyBigWorldHelpCourseUnlock>(harness, nameof(NotifyBigWorldHelpCourseUnlock), "help push 2").Data.IsRead, "help unlock with IsRead");
            AssertEqual(25200014, Req<BigWorldHelpCourseUnlockResponse>("BigWorldHelpCourseUnlockRequest", new BigWorldHelpCourseUnlockRequest { CourseId = 1010 }).Code, "help unlock twice");
            AssertEqual(0, Req<BigWorldHelpCourseReadResponse>("BigWorldHelpCourseReadRequest", new BigWorldHelpCourseReadRequest { CourseId = 1010 }).Code, "help read");
            AssertEqual(25200016, Req<BigWorldHelpCourseReadResponse>("BigWorldHelpCourseReadRequest", new BigWorldHelpCourseReadRequest { CourseId = 1010 }).Code, "help read twice");
            NoPacket("archive end");

            // ---- PlayerData from a reloaded save ----
            BigWorldPlayerData data = new();
            Call("FillPlayerData", Reload(), data);
            AssertEqual((filterId, 1, 2, 2, true), (data.BigWorldPhotographData.UnlockedCameraFilters.Single(), data.BigWorldMessageDict[1001].State,
                data.NewsPopupData.Count, data.BigWorldHelpCourseList.Count, data.BigWorldHelpCourseList.All(course => course.IsRead)), "player data after reload");
            MessagePackSerializer.Serialize(data);
            Console.WriteLine("big world archive: ok");
        }

        private static void AssertThrows<TException>(Action action, string name) where TException : Exception
        {
            try { action(); }
            catch (TException) { return; }
            throw new InvalidDataException($"{name}: expected {typeof(TException).Name}.");
        }
    }
}
