using System;
using System.IO;
using System.Net;
using System.Web;
using System.Xml;
using System.Text;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;

namespace KedamaQueryBot
{
    class Program
    {
        //工作目录
        static string appPath = Environment.CurrentDirectory + "\\";
        //配置文件路径
        static string configFile = appPath + "kedamaQueryBot.conf";
        //日志文件路径
        static string logFile = appPath + "kedamaQueryBot.log";
        //bot的用户名，带@
        static string botUsername = "@kedama_localbot";
        //查询玩家/方块/物品时允许使用的 ID 字符集
        static string idAllowedCharset = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

        enum dataVersion { MC112, MC1132 }
        struct tgMsg { public string userName, scrName, msgID, msgText; }

        /* TODO：
         * 1. 进度判断不能依赖节点下done节点的内容，因某些玩家的数据是在旧版本时生成的，满足了当时的进度条件，
         *    但实际上没有满足当前服务器版本的完成条件，比较保险的办法仍然是按照目前服务器版本的完成条件逐一检查。（已解决）
         * 2. 『探索的时光』在1.13以前具有不同的生物群系名称；『成双成对』具备两种版本的动物名称。（已解决）
         * 3. 重写单数条件进度判断，使代码可以复用。（已解决）
         * 4. itemlist 只有方块，没有物品的 ID，需要补充。（已解决）
         * 5. 优化日志记录，raw message 记录消息发送者的名字和ID。（已解决）
         */

        static void Main(string[] args)
        {
            /* - - - - - - - 公用对象相关 - - - - - - - */

            //临时字符串
            string tmp;
            //XML对象
            XmlDocument x; XmlNode xN; XmlNodeList xNL;

            /* - - - - - - - 配置文件相关 - - - - - - - */

            //数据目录
            string dataPath = string.Empty;
            //代理服务器 IP、端口
            IPAddress proxyHost; int proxyPort;
            //Bot Token
            string botToken; long msgOffsetID; int tgQueryInterval;

            /* - - - - - - - 运行过程相关 - - - - - - - */

            //冷启动标志位、启动时间、本次启动响应次数
            bool coldBoot = true;
            DateTime startupTime = DateTime.Now;
            long replyCount = 0;
            //玩家ID - UUID 对照表
            Dictionary<string, string> playerID_UUID = new Dictionary<string, string>();
            //物品ID - 中文名称对照表
            Dictionary<string, string> itemID_Name;
            //TG 消息（对话）ID - 消息内容对照表
            ArrayList msgArray = new ArrayList();
            //目前可用的数据文件日期前缀
            string availableDatePrefix = string.Empty;
            //玩家/方块/物品 ID 查询可用字符集
            List<string> idAllowedChar = new List<string>(idAllowedCharset.Length);

            /* 程序开始 */

            //初始化配置变量
            proxyHost = IPAddress.Parse("127.0.0.1");
            proxyPort = 0;
            botToken = string.Empty;
            msgOffsetID = 0;
            tgQueryInterval = 0;
            //加载或初始化配置文件，如果出错则退出
            if (loadSettings(configFile, ref dataPath,
                             ref proxyHost, ref proxyPort, ref botToken,
                             ref msgOffsetID, ref tgQueryInterval) == false)
            { AnyKeyExit(); return; }

            //加载物品ID-名称列表
            itemID_Name = getItemIDtoNameList();
            if (itemID_Name.Count > 0)
            {
                WriteCon("已加载 " + itemID_Name.Count + " 个物品 ID - 名称条目");
            }
            else
            {
                WriteCon("加载物品 ID - 名称列表时出错，请检查文件内容");
                AnyKeyExit(); return;
            }
            //初始化 ID 可用字符集
            for (int n = 0; n < idAllowedCharset.Length; n++)
            { idAllowedChar.Add(idAllowedCharset.Substring(n, 1)); }
            if (idAllowedChar.Count > 0)
            {
                WriteCon("已加载 " + idAllowedChar.Count + " 个 ID 可用字符");
            }
            else
            {
                WriteCon("加载 ID 可用字符集时出错，请检查文件内容");
            }

            //轮训、响应请求
            while (true)
            {
                try
                {
                    //是否为冷启动，是则初始化玩家ID-UUID列表，并立即开始执行轮询，否则在每两次轮询间等待
                    if (coldBoot == true)
                    {
                        procBuildPlayerToUUID(dataPath, ref playerID_UUID);
                        WriteCon("开始轮询请求", true);
                        coldBoot = false;
                    }
                    else
                    {
                        Thread.Sleep(tgQueryInterval * 1000);
                    }
                    //每次轮询前清空消息列表
                    msgArray.Clear();
                    //正常情况下，下载服务应在每天早上最晚8点15分完成数据下载，
                    //故如果超过该时间，且数据目录下没有当日的 XML，
                    //则清空数据目录下的 XML 和 availableDatePrefix，以迫使相应流程拉取新数据
                    if (DateTime.Now.Hour == 8 && DateTime.Now.Minute > 14
                        && Directory.GetFiles(dataPath, DateTime.Now.ToString("yyyy-MM-dd") + "_*.xml").Length < 1)
                    {
                        foreach (string f in Directory.GetFiles(dataPath, "*.xml")) { File.Delete(f); }
                        availableDatePrefix = string.Empty;
                    }
                    //从服务器拉取信息
                    x = tgGET("getUpdates", (msgOffsetID > 0 ? "offset=" + (msgOffsetID + 1).ToString() : string.Empty), botToken, proxyHost, proxyPort, false);
                    if (x == null) continue;
                    if (string.IsNullOrEmpty(x.InnerXml)) continue;
                    xN = x.SelectSingleNode("/data/ok");
                    if (xN != null)
                    {
                        if (xN.InnerText.ToLower() == "true")
                        {
                            int updatesCount = 0; tgMsg msg;
                            xNL = x.SelectNodes("/data/result"); updatesCount = xNL.Count;
                            if (updatesCount > 0)
                            {
                                WriteCon("获取到 " + updatesCount + " 条新消息　　　　　　　　　　", true);
                                //保存最新消息的 Offset ID
                                msgOffsetID = long.Parse(x.SelectSingleNode("/data/result[" + updatesCount + "]/update_id").InnerText);
                                SaveSettings(configFile, dataPath, proxyHost, proxyPort, botToken, msgOffsetID, tgQueryInterval);
                                //遍历新消息，提取 Chat ID 和消息内容备用
                                foreach (XmlNode xN_Result in xNL)
                                {
                                    if (xN_Result.SelectSingleNode("message/text") == null) continue;
                                    msg = new tgMsg();
                                    msg.msgID = xN_Result.SelectSingleNode("message/chat/id").InnerText;
                                    msg.userName = xN_Result.SelectSingleNode("message/from/username").InnerText;
                                    msg.scrName = (xN_Result.SelectSingleNode("message/from/first_name") == null ? "" : xN_Result.SelectSingleNode("message/from/first_name").InnerText)
                                                 + (xN_Result.SelectSingleNode("message/from/last_name") == null ? "" : xN_Result.SelectSingleNode("message/from/last_name").InnerText);
                                    msg.msgText = xN_Result.SelectSingleNode("message/text").InnerText;
                                    msgArray.Add(msg);
                                }
                            }
                            else
                            {
                                WriteConAndCR("更新完成，暂时没有新消息\r");
                            }
                        }
                        else
                        {
                            WriteCon("轮训请求失败：服务器返回错误信息", true);
                            WriteCon("错误信息（*" + x.InnerXml.Length + "）：" + x.SelectSingleNode("/data/description").InnerText, true);
                        }
                    }
                    else
                    {
                        WriteCon("轮询请求失败：/data/ok 节点遗失，可能为超时或其它错误", true);
                        WriteCon("返回信息（" + x.InnerXml.Length + "）：" + x.InnerXml, true);
                        continue;
                    }
                    //处理消息
                    if (msgArray.Count > 0)
                    {
                        string currentChatID, msg;
                        string[] msgParts = new string[1];
                        string[] cmdArgs = new string[0];
                        foreach (tgMsg msgObj in msgArray)
                        {
                            currentChatID = msgObj.msgID;
                            msg = msgObj.msgText;
                            WriteLog("获取消息：发送者 [ " + msgObj.scrName + " ] ( " + msgObj.userName + " )，消息 ID = " + msgObj.msgID + "，内容 = " + msgObj.msgText);
                            //删除 inLine Call 中本 Bot 的 ID
                            if (msg.Contains("@"))
                            {
                                if (msg.Substring(msg.LastIndexOf("@")).ToLower() == botUsername)
                                    msg = msg.Substring(0, msg.LastIndexOf("@"));
                            }
                            //分离指令和参数，并将参数存入cmdArgs[]
                            if (msg.IndexOf(" ") > 0)
                            {
                                msgParts = msg.Split(' ');
                                cmdArgs = new string[msgParts.Length - 1];
                                for (int argN = 1; argN < msgParts.Length; argN++) { cmdArgs[argN - 1] = msgParts[argN]; }
                            }
                            else
                            {
                                msgParts[0] = msg;
                            }
                            //响应次数 + 1
                            replyCount += 1;
                            //判断指令
                            switch (msgParts[0])
                            {
                                case "/start":
                                    //主菜单
                                    tgSendMessage(currentChatID, getReplyContent("start"), botToken, proxyHost, proxyPort);
                                    break;
                                case "/player":
                                    //玩家基础数据查询
                                    string playerID2Display_Player, playerID2Query_Player;
                                    //参数数量检查
                                    if (cmdArgs.Length < 1)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("player_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //玩家 ID 参数检查
                                    cmdArgs[0] = sanitizeID(cmdArgs[0], ref idAllowedChar);
                                    playerID2Query_Player = cmdArgs[0].Trim().ToLower();
                                    if (playerID2Query_Player.Length < 1)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("player_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    playerID2Display_Player = cmdArgs[0].Replace("_", "\\_");
                                    //玩家数据文件存在性检查
                                    tmp = procGetPlayerDataFileName(playerID2Query_Player, dataPath, ref availableDatePrefix, ref playerID_UUID);
                                    if (string.IsNullOrEmpty(tmp))
                                    {
                                        //当夹在 * * 中间时，不需要对 _ 进行特殊处理
                                        playerID2Display_Player = playerID2Display_Player.Replace("\\_", "_");
                                        tmp = getReplyContent("player_notfound");
                                        tmp = tmp.Replace("{player_id}", playerID2Display_Player);
                                        tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //读取数据、组合响应文本、发送响应
                                    x = new XmlDocument();
                                    x.Load(dataPath + tmp);
                                    if (x.SelectSingleNode("/data/data") == null)
                                    {
                                        tgSendMessage(currentChatID, makeErrorMsgToUser("数据结构已发生变化"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    tmp = getReplyContent("player");
                                    tmp = tmp.Replace("{uuid}", x.SelectSingleNode("/data/data/uuid").InnerText);
                                    tmp = tmp.Replace("{player_id}", x.SelectSingleNode("/data/data/playername").InnerText);
                                    DateTime dplayerCreate = TimestampMStoDateTime(x.SelectSingleNode("/data/data/time_start").InnerText);
                                    int iPlayerCreatedDays = (int)((DateTime.Now - dplayerCreate).TotalDays);
                                    tmp = tmp.Replace("{time_create}", DateTimeToStr(dplayerCreate));
                                    tmp = tmp.Replace("{days_created}", iPlayerCreatedDays + " 天");
                                    tmp = tmp.Replace("{time_lastseen}", DateTimeToStr(TimestampMStoDateTime(x.SelectSingleNode("/data/data/time_last").InnerText)));
                                    tmp = tmp.Replace("{time_playtime}", SecToHour(x.SelectSingleNode("/data/data/time_lived").InnerText));
                                    tmp = tmp.Replace("{is_banned}", (x.SelectSingleNode("/data/data/banned").InnerText.ToLower() == "true" ? "是" : "否"));
                                    xNL = x.SelectNodes("/data/data/names");
                                    if (xNL.Count > 1)
                                    {
                                        string historyNames = "\r\n";
                                        for (int n = 0; n < xNL.Count; n++)
                                        {
                                            historyNames = historyNames + "> *" + xNL[n].SelectSingleNode("name").InnerText + "*";
                                            if (xNL[n].SelectSingleNode("changedToAt") == null)
                                            {
                                                historyNames = historyNames + "（初始名称）";
                                            }
                                            else
                                            {
                                                historyNames = historyNames + "（" + TimestampMStoDateTime(xNL[n].SelectSingleNode("changedToAt").InnerText) + "）";
                                                if (n < xNL.Count) historyNames = historyNames + "\r\n";
                                            }
                                        }
                                        tmp = tmp.Replace("{history_names}", historyNames);
                                    }
                                    else
                                    {
                                        tmp = tmp.Replace("{history_names}", "该玩家尚未修改过名字");
                                    }
                                    tmp = tmp.Replace("{data_time}", getDataTime(ref x));
                                    tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                    break;
                                case "/adv":
                                    //组合成就查询
                                    string playerID2Display_Adv, playerID2Query_Adv; int advIDtoQuery;
                                    string nodeRoot; bool hasUnfinishedAdv;
                                    Dictionary<string, string> advCreterias;
                                    //参数数量检查
                                    if (cmdArgs.Length < 2)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("adv_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //玩家 ID 参数检查
                                    //由于下划线在 Markdown 中有斜体的特殊含义，故需要增加 \ 以正常发送数据
                                    cmdArgs[0] = sanitizeID(cmdArgs[0], ref idAllowedChar);
                                    playerID2Display_Adv = cmdArgs[0].Replace("_", "\\_");
                                    playerID2Query_Adv = cmdArgs[0].Trim().ToLower();
                                    if (playerID2Query_Adv.Length < 1)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("adv_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //玩家数据文件存在性检查
                                    tmp = procGetPlayerDataFileName(playerID2Query_Adv, dataPath, ref availableDatePrefix, ref playerID_UUID);
                                    if (string.IsNullOrEmpty(tmp))
                                    {
                                        tmp = getReplyContent("player_notfound");
                                        tmp = tmp.Replace("{player_id}", playerID2Display_Adv);
                                        tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //成就序号检查
                                    if (int.TryParse(cmdArgs[1], out advIDtoQuery) == false)
                                    {
                                        tmp = getReplyContent("adv_invalid_id");
                                        tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //读取数据、组合响应文本、发送响应
                                    x = new XmlDocument();
                                    x.Load(dataPath + tmp);
                                    if (x.SelectSingleNode("/data/advancements") == null)
                                    {
                                        tgSendMessage(currentChatID, makeErrorMsgToUser("数据结构已发生变化"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    switch (advIDtoQuery)
                                    {
                                        case 1:
                                            //狂乱的鸡尾酒（单判断条件）
                                            doCheckSingleCretAdvancement(ref x, "all_potions", "nether_all_potions",
                                                playerID2Display_Adv, currentChatID, botToken, proxyHost, proxyPort);
                                            break;
                                        case 2:
                                            //为什么会变成这样呢（单判断条件）
                                            doCheckSingleCretAdvancement(ref x, "all_effects", "nether_all_effects",
                                                playerID2Display_Adv, currentChatID, botToken, proxyHost, proxyPort);
                                            break;
                                        case 3:
                                            //怪物狩猎完成（复合判断条件）
                                            doCheckMultiCretAdvancement(ref x, "kill_all_mobs", "怪物狩猎完成", "adventure_kill_all_mobs",
                                                playerID2Display_Adv, currentChatID, botToken, proxyHost, proxyPort);
                                            break;
                                        case 4:
                                            //探索的时光（复合判断条件，特殊处理）
                                            xN = x.SelectSingleNode("/data/advancements/adventure_adventuring_time");
                                            if (xN == null)
                                            {
                                                tmp = getReplyContent("adv_none_adventuring_time");
                                            }
                                            else
                                            {
                                                tmp = getReplyContent("adv_intro_adventuring_time");
                                                advCreterias = getAdvCreteriaList("adventuring_time");
                                                Dictionary<string, string> advCret_AdventuringTime_Before1_13 = getAdvCreteriaList("adventuring_time_before113");
                                                nodeRoot = "/data/advancements/adventure_adventuring_time/criteria";
                                                StringBuilder lns = new StringBuilder();
                                                hasUnfinishedAdv = false;

                                                //判断玩家最后登录是否在毛线 1.13.2 更新前，由于生物群系名称的大变动，如果是则无法显示该进度的完成情况
                                                long kedama113UpdateTime = DateTimeToTimeStampMS(DateTime.Parse("2018-11-04 00:00:00"));
                                                long playerLastSeen = long.Parse(x.SelectSingleNode("/data/data/time_last").InnerText);
                                                if (playerLastSeen < kedama113UpdateTime)
                                                {
                                                    tmp = getReplyContent("adv_err_adventuring_time");
                                                }
                                                else
                                                {
                                                    //检查进度完成情况
                                                    if (advCreterias.Count > 0)
                                                    {
                                                        foreach (KeyValuePair<string, string> advCret in advCreterias)
                                                        {
                                                            if (isNodeAvailable(ref x, nodeRoot + "/" + advCret.Key) == false)
                                                            { hasUnfinishedAdv = true; lns.AppendLine("> " + advCret.Value); }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        tgSendMessage(currentChatID, makeErrorMsgToUser("读取『探索的时光』成就列表时发生错误"), botToken, proxyHost, proxyPort);
                                                        break;
                                                    }
                                                    //检查玩家的进度完成条件中是否包含 1.13.2 以前的群系名称，
                                                    //以避免出现虽然玩家在 1.13.2 更新后登录过，但数据有偏差的情况
                                                    if (advCret_AdventuringTime_Before1_13.Count > 0)
                                                    {
                                                        foreach (KeyValuePair<string, string> oldBiomeTest in advCret_AdventuringTime_Before1_13)
                                                        {
                                                            if (isNodeAvailable(ref x, nodeRoot + "/" + oldBiomeTest.Key))
                                                            {
                                                                lns.AppendLine("*注意*：由于不明原因，该玩家在 1.13.2 更新后的进度数据中仍包含 1.13.2 之前的生物群系名称，以上结果可能不准确。");
                                                                break;
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        lns.AppendLine("_（加载 1.13.2 旧式生物群系列表时发生错误）_");
                                                    }
                                                    //如果有未完成进度，替换占位符，否则重新载入已完成信息模板
                                                    if (hasUnfinishedAdv == true) { tmp = tmp.Replace("{unfinished_content}", lns.ToString()); }
                                                    else { tmp = getReplyContent("adv_done_adventuring_time"); }
                                                }
                                            }
                                            //统一替换玩家ID、数据时间
                                            tmp = tmp.Replace("{player_id}", playerID2Display_Adv);
                                            tmp = tmp.Replace("{data_time}", getDataTime(ref x));
                                            tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                            break;
                                        case 5:
                                            //成双成对（复合判断条件）
                                            doCheckMultiCretAdvancement(ref x, "bred_all_animals", "成双成对", "husbandry_bred_all_animals",
                                                playerID2Display_Adv, currentChatID, botToken, proxyHost, proxyPort);
                                            break;
                                        case 6:
                                            //均衡饮食（复合判断条件）
                                            doCheckMultiCretAdvancement(ref x, "balanced_diet", "成双成对", "husbandry_balanced_diet",
                                                playerID2Display_Adv, currentChatID, botToken, proxyHost, proxyPort);
                                            break;
                                        default:
                                            tmp = getReplyContent("adv_invalid_id");
                                            tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                            break;
                                    }
                                    break;
                                case "/item":
                                    //玩家方块、物品互动数量查询
                                    string playerID2Display_Item, playerID2Query_Item;
                                    string itemID2Display, itemID2Query, itemName; dataVersion playerDataVersion;
                                    string itemNodeRoot = "/data/stats";
                                    bool gotValidDataVer = false; string dataVersionStr;
                                    bool isNoNodeFound = false;
                                    long nMined = -1, nCrafted = -1, nUsed = -1, nPickedUp = -1, nDropped = -1, nBroken = -1;
                                    StringBuilder output = new StringBuilder();
                                    //参数数量检查
                                    if (cmdArgs.Length < 2)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("item_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //由于下划线在 Markdown 中有斜体的特殊含义，故需要增加 \ 以正常发送数据
                                    cmdArgs[1] = sanitizeID(cmdArgs[1], ref idAllowedChar);
                                    itemID2Display = cmdArgs[1].Replace("_", "\\_");
                                    itemID2Query = cmdArgs[1];
                                    //玩家 ID 参数检查
                                    //由于下划线在 Markdown 中有斜体的特殊含义，故需要增加 \ 以正常发送数据
                                    playerID2Display_Item = cmdArgs[0].Replace("_", "\\_");
                                    playerID2Query_Item = cmdArgs[0].Trim().ToLower();
                                    if (playerID2Query_Item.Length < 1)
                                    {
                                        tgSendMessage(currentChatID, getReplyContent("item_intro"), botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //玩家数据文件存在性检查
                                    tmp = procGetPlayerDataFileName(playerID2Query_Item, dataPath, ref availableDatePrefix, ref playerID_UUID);
                                    if (string.IsNullOrEmpty(tmp))
                                    {
                                        tmp = getReplyContent("player_notfound");
                                        tmp = tmp.Replace("{player_id}", playerID2Query_Item);
                                        tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                        break;
                                    }
                                    //加载数据
                                    x = new XmlDocument();
                                    x.Load(dataPath + tmp);
                                    //判断数据版本
                                    playerDataVersion = getDataVersion(ref x);
                                    switch (playerDataVersion)
                                    {
                                        case dataVersion.MC1132:
                                            gotValidDataVer = true;
                                            //检查某ID的任一行为的节点是否存在，如果不存在则在下面的流程中直接跳出
                                            isNoNodeFound = !(
                                                isNodeAvailable(ref x, itemNodeRoot + "/mined_" + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/crafted_" + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/used_" + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/broken_" + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/picked_up_" + itemID2Query));
                                            //尝试读取数据，不存在的节点获取到的值将为-1
                                            nMined = getNodeValueInLong(ref x, itemNodeRoot + "/mined_" + itemID2Query);
                                            nCrafted = getNodeValueInLong(ref x, itemNodeRoot + "/crafted_" + itemID2Query);
                                            nUsed = getNodeValueInLong(ref x, itemNodeRoot + "/used_" + itemID2Query);
                                            nPickedUp = getNodeValueInLong(ref x, itemNodeRoot + "/picked_up_" + itemID2Query);
                                            nBroken = getNodeValueInLong(ref x, itemNodeRoot + "/broken_" + itemID2Query);
                                            nDropped = getNodeValueInLong(ref x, itemNodeRoot + "/dropped_" + itemID2Query);
                                            dataVersionStr = "Minecraft 1.13.2";
                                            break;
                                        case dataVersion.MC112:
                                            gotValidDataVer = true;
                                            //检查某ID的任一行为的节点是否存在，如果不存在则在下面的流程中直接跳出
                                            isNoNodeFound = !(
                                                isNodeAvailable(ref x, itemNodeRoot + "/stat.mineBlock.minecraft." + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/stat.craftItem.minecraft." + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/stat.useItem.minecraft." + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/stat.breakItem.minecraft." + itemID2Query) ||
                                                isNodeAvailable(ref x, itemNodeRoot + "/stat.pickup.minecraft." + itemID2Query));
                                            //尝试读取数据，不存在的节点获取到的值将为-1
                                            nMined = getNodeValueInLong(ref x, itemNodeRoot + "/stat.mineBlock.minecraft." + itemID2Query);
                                            nCrafted = getNodeValueInLong(ref x, itemNodeRoot + "/stat.craftItem.minecraft." + itemID2Query);
                                            nUsed = getNodeValueInLong(ref x, itemNodeRoot + "/stat.useItem.minecraft." + itemID2Query);
                                            nPickedUp = getNodeValueInLong(ref x, itemNodeRoot + "/stat.pickup.minecraft." + itemID2Query);
                                            nBroken = getNodeValueInLong(ref x, itemNodeRoot + "/stat.breakItem.minecraft." + itemID2Query);
                                            nDropped = getNodeValueInLong(ref x, itemNodeRoot + "/stat.drop.minecraft." + itemID2Query);
                                            dataVersionStr = "Minecraft 1.12";
                                            break;
                                        default:
                                            gotValidDataVer = false;
                                            dataVersionStr = "未知";
                                            break;
                                    }
                                    //如果是支持的版本，准备待发送文本并处理，否则发送错误信息
                                    if (gotValidDataVer == true)
                                    {
                                        //是否没有找到任何节点，可能原因是提供的物品/方块 ID 不正确
                                        if (isNoNodeFound == true)
                                        {
                                            tmp = getReplyContent("item_nodata");
                                            tmp = tmp.Replace("{block_name}", itemID2Query);
                                        }
                                        else
                                        {
                                            tmp = getReplyContent("item_result");
                                            switch (playerDataVersion)
                                            {
                                                case dataVersion.MC1132:
                                                    if (itemID_Name.ContainsKey(itemID2Query)) { itemName = itemID_Name[itemID2Query]; break; }
                                                    if (itemID_Name.ContainsKey("-" + itemID2Query)) { itemName = itemID_Name["-" + itemID2Query]; break; }
                                                    itemName = "*物品名称缺失，请联系管理员*"; break;
                                                case dataVersion.MC112:
                                                    if (itemID_Name.ContainsKey("~" + itemID2Query)) { itemName = itemID_Name["~" + itemID2Query]; break; }
                                                    if (itemID_Name.ContainsKey(itemID2Query)) { itemName = itemID_Name[itemID2Query]; break; }
                                                    itemName = "*物品名称缺失，请联系管理员*"; break;
                                                default:
                                                    itemName = "*物品名称缺失，请联系管理员*"; break;
                                            }
                                            tmp = tmp.Replace("{item_name}", itemName);
                                            StringBuilder blockStat = new StringBuilder();
                                            if (nMined > 0) blockStat.AppendLine("挖掘：" + nMined + " 个");
                                            if (nCrafted > 0) blockStat.AppendLine("合成：" + nCrafted + " 个");
                                            if (nUsed > 0) blockStat.AppendLine("使用：" + nUsed + " 次");
                                            if (nPickedUp > 0) blockStat.AppendLine("拾起：" + nPickedUp + " 次");
                                            if (nDropped > 0) blockStat.AppendLine("丢弃：" + nDropped + " 次");
                                            if (nBroken > 0) blockStat.AppendLine("损坏：" + nDropped + " 次");
                                            if (blockStat.Length > 0) { tmp = tmp.Replace("{block_stat}", blockStat.ToString()); }
                                            else { tmp = tmp.Replace("{block_stat}", "此玩家暂无该方块 / 物品的相关记录"); }
                                        }
                                        //统一替换玩家ID、方块/物品ID、数据日期和数据版本，并发送回复
                                        tmp = tmp.Replace("{player_id}", playerID2Display_Item);
                                        tmp = tmp.Replace("{item_id}", itemID2Display);
                                        tmp = tmp.Replace("{data_time}", getDataTime(ref x));
                                        tmp = tmp.Replace("{data_version}", dataVersionStr);
                                        tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                    }
                                    else { tgSendMessage(currentChatID, makeErrorMsgToUser("未知数据版本"), botToken, proxyHost, proxyPort); }
                                    break;
                                case "/mob":
                                    //玩家与生物互动数量查询
                                    break;
                                case "/status":
                                    //系统运行状态查询
                                    tmp = getReplyContent("status");
                                    tmp = tmp.Replace("{startup_time}", DateTimeToStr(startupTime));
                                    long upTime = (int)((DateTime.Now - startupTime).TotalSeconds);
                                    tmp = tmp.Replace("{uptime}", SecToHour(upTime.ToString()));
                                    tmp = tmp.Replace("{reply_time}", replyCount.ToString());
                                    tgSendMessage(currentChatID, tmp, botToken, proxyHost, proxyPort);
                                    break;
                                case "/gugu":
                                    //咕咕语录
                                    tgSendMessage(currentChatID, getRandomDictLine("gugu"), botToken, proxyHost, proxyPort);
                                    break;
                                case "/syn":
                                    //ACK
                                    tgSendMessage(currentChatID, "ACK desu", botToken, proxyHost, proxyPort);
                                    break;
                                case "/ping":
                                    //Ping
                                    tgSendMessage(currentChatID, "♪", botToken, proxyHost, proxyPort);
                                    break;
                                case "/yahoo":
                                    //Yahoo
                                    tgSendMessage(currentChatID, "呀嚯～", botToken, proxyHost, proxyPort);
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteCon("发生未捕获的异常", true);
                    WriteCon("详细信息：" + ex.ToString(), true);
                }
            }

            //托底
            AnyKeyExit(); return;
        }

        /* - - - - - - - - - - 程序自用方法开始 - - - - - - - - - - */

        //加载或初始化配置
        static bool loadSettings(string configFile, 
                                 ref string dataPath, ref IPAddress proxyHost, ref int proxyPort, 
                                 ref string botToken, ref long msgOffsetID, ref int tgQueryInterval)
        {
            XmlDocument x;XmlNode xN; string tmp;
            if (File.Exists(configFile))
            {
                //加载配置文件
                x = new XmlDocument();
                try { x.Load(configFile); }
                catch (Exception ex)
                {
                    WriteCon("加载配置文件时发生错误");
                    WriteCon("错误信息：" + ex.Message);
                    return false;
                }

                //加载并校验配置：数据来源
                xN = x.SelectSingleNode("/conf/datapath");
                try
                {
                    if (xN == null)
                    {
                        WriteCon("配置文件内容无效：找不到 /conf/datapath 节点");
                        throw new Exception();
                    }
                    tmp = xN.InnerText;
                    if (Directory.Exists(tmp) == false)
                    {
                        WriteCon("配置文件内容无效：下载服务 today 临时文件目录不存在");
                        throw new Exception();
                    }
                }
                catch
                {
                    return false;
                }
                dataPath = xN.InnerText;
                if (dataPath.Substring(dataPath.Length - 1, 1) != "\\") dataPath = dataPath + "\\";
                WriteCon("数据路径：" + dataPath);

                //加载并校验配置：代理服务器
                tmp = string.Empty;
                xN = x.SelectSingleNode("/conf/proxy");
                try
                {
                    if (xN == null)
                    {
                        WriteCon("配置文件内容无效：找不到 /conf/proxy 节点");
                        throw new Exception();
                    }
                    tmp = xN.InnerText;
                    string[] tmpSplit = tmp.Split(':');
                    if (tmpSplit.Length < 2)
                    {
                        WriteCon("配置文件内容无效：代理服务器格式无效");
                        throw new Exception();
                    }
                    if (IPAddress.TryParse(tmpSplit[0], out proxyHost) == false)
                    {
                        WriteCon("配置文件内容无效：代理服务器IP地址无效");
                        throw new Exception();
                    }
                    if (int.TryParse(tmpSplit[1], out proxyPort) == false)
                    {
                        WriteCon("配置文件内容无效：代理服务器端口无效");
                        throw new Exception();
                    }
                    if (proxyPort < 1 || proxyPort > 65535)
                    {
                        WriteCon("配置文件内容无效：代理服务器端口无效");
                        throw new Exception();
                    }
                }
                catch
                {
                    return false;
                }
                WriteCon("连接代理：" + proxyHost.ToString() + ":" + proxyPort);

                //加载并校验配置：Bot Token
                tmp = string.Empty;
                xN = x.SelectSingleNode("/conf/bot_token");
                try
                {
                    if (xN == null)
                    {
                        WriteCon("配置文件内容无效：找不到 /conf/bot_token 节点");
                        throw new Exception();
                    }
                    tmp = xN.InnerText;
                }
                catch
                {
                    return false;
                }
                botToken = tmp;
                WriteCon("Bot Token：" + botToken);

                //加载并校验配置：最新消息 Offset ID
                tmp = string.Empty;
                xN = x.SelectSingleNode("/conf/msgOffsetID");
                try
                {
                    if (xN == null)
                    {
                        WriteCon("配置文件内容无效：找不到 /conf/msgOffsetID 节点");
                        throw new Exception();
                    }
                    tmp = xN.InnerText;
                    if (string.IsNullOrEmpty(tmp)) tmp = "0";
                    if (long.TryParse(tmp, out msgOffsetID) == false)
                    {
                        WriteCon("配置文件内容无效：msgOffsetID 不是长整形");
                        throw new Exception();
                    }
                }
                catch
                {
                    return false;
                }
                WriteCon("消息 Offset ID：" + (msgOffsetID > 0 ? msgOffsetID.ToString() : "无"));

                //加载并校验配置：Bot API 两次轮询间等待时间（秒）
                tmp = string.Empty;
                xN = x.SelectSingleNode("/conf/tgQueryInterval");
                try
                {
                    if (xN == null)
                    {
                        WriteCon("配置文件内容无效：找不到 /conf/tgQueryInterval 节点");
                        throw new Exception();
                    }
                    tmp = xN.InnerText;
                    if (string.IsNullOrEmpty(tmp)) tmp = "5";
                    if (int.TryParse(tmp, out tgQueryInterval) == false)
                    {
                        WriteCon("配置文件内容无效：tgQueryInterval 不是整型或超出范围");
                        throw new Exception();
                    }
                    if (tgQueryInterval < 1 || tgQueryInterval > 60) tgQueryInterval = 5;
                }
                catch
                {
                    return false;
                }
                WriteCon("轮询等待：" + tgQueryInterval.ToString() + "秒");
                //全部配置信息加载并验证完成
                return true;
            }
            else
            {
                //初始化配置文件
                WriteCon("配置文件不存在，进行初始化");

                //数据目录
                string dlSrvTodayPath = string.Empty;
                while (true)
                {
                    Console.Write("下载服务 today 临时文件路径：");
                    dlSrvTodayPath = Console.ReadLine().Trim();
                    if (string.IsNullOrEmpty(dlSrvTodayPath)) continue;
                    if (Directory.Exists(dlSrvTodayPath) == false)
                    {
                        WriteCon("输入错误，指定的路径不存在");
                        continue;
                    }
                    break;
                }

                //代理服务器地址
                string tmpProxyAddr; string[] tmpProxyAddrSplit;
                IPAddress tmpProxyIP; int tmpProxyPort;
                while (true)
                {
                    Console.Write("代理服务器地址（IP:端口）：");
                    tmpProxyAddr = Console.ReadLine().Trim();
                    if (string.IsNullOrEmpty(tmpProxyAddr)) continue;
                    tmpProxyAddrSplit = tmpProxyAddr.Split(':');
                    if (tmpProxyAddrSplit.Length != 2)
                    {
                        WriteCon("输入错误，代理服务器地址格式不正确");
                        continue;
                    }
                    try { tmpProxyIP = IPAddress.Parse(tmpProxyAddrSplit[0]); }
                    catch
                    {
                        WriteCon("输入错误，代理服务器IP格式不正确");
                        continue;
                    }
                    try
                    {
                        tmpProxyPort = int.Parse(tmpProxyAddrSplit[1]);
                        if (tmpProxyPort < 1 || tmpProxyPort > 65535) throw new Exception();
                    }
                    catch
                    {
                        WriteCon("输入错误，代理服务器端口格式不正确");
                        continue;
                    }
                    break;
                }

                //Bot Token
                string tmpBotToken;
                while (true)
                {
                    Console.Write("Bot Token：");
                    tmpBotToken = Console.ReadLine().Trim();
                    if (string.IsNullOrEmpty(tmpBotToken) == false) break;
                }
                WriteCon("保存配置文件...");
                SaveSettings(configFile, dlSrvTodayPath, tmpProxyIP, tmpProxyPort, tmpBotToken, 0, 5);
                return false;
            }
        }

        //生成应答用户的错误信息
        static string makeErrorMsgToUser(string s)
        {
            return "发生错误，请稍后再试（" + s + "）。";
        }

        //发送消息
        static XmlDocument tgSendMessage(string chatID, string text, string botToken, IPAddress proxyHost, int proxyPort)
        {
            return tgPOST("sendMessage", "chat_id=" + chatID + "&parse_mode=Markdown&disable_notification=true&text=" + text, botToken, proxyHost, proxyPort);
        }

        //生成 Bot API 调用 URL，调用 GET 并返回结果
        static XmlDocument tgGET(string funcName, string queryStr, string botToken, IPAddress proxyHost, int proxyPort, bool allowLongPoll)
        {
            return JSONtoXML(httpGET("https://api.telegram.org/bot" + botToken + "/" + funcName, queryStr, proxyHost, proxyPort, allowLongPoll));
        }

        static XmlDocument tgPOST(string funcName, string postData, string botToken, IPAddress proxyHost, int proxyPort)
        {
            return JSONtoXML(httpPOST("https://api.telegram.org/bot" + botToken + "/" + funcName, postData, proxyHost, proxyPort));
        }

        //获得指定玩家数据的文件名
        static string procGetPlayerDataFileName(string playerID, string dataPath, ref string availableDatePrefix, ref Dictionary<string, string> playerID_UUID)
        {
            //如果数据目录下没有任何 XML 文件，调用对应方法尝试生成
            if (Directory.GetFiles(dataPath, "*.xml").Length == 0)
            {
                WriteCon("没有可用的 XML 格式文件，正在生成新文件...", true);
                if (procBuildPlayerToUUID(dataPath, ref playerID_UUID) == false) return string.Empty;
            }
            //如果没有提供可用的数据日期前缀，查找距离今天日期最近的数据文件，并更新 Main 中对应变量
            if (string.IsNullOrEmpty(availableDatePrefix))
            {
                DateTime availableDataDate = DateTime.Now;
                while (true)
                {
                    availableDatePrefix = availableDataDate.ToString("yyyy-MM-dd") + "_";
                    if (Directory.GetFiles(dataPath, availableDatePrefix + "*.xml").Length > 0) break;
                    availableDataDate = availableDataDate.AddDays(-1);
                }
            }
            //如果 ID - UUID 列表为空，从 XML 文件生成列表
            if (playerID_UUID.Count == 0)
            {
                XmlDocument x;
                foreach (string fPath in Directory.GetFiles(dataPath, availableDatePrefix + "*.xml"))
                {
                    x = new XmlDocument(); x.Load(fPath);
                    if (x.SelectSingleNode("/data/data/playername") != null)
                    {
                        playerID_UUID.Add(x.SelectSingleNode("/data/data/playername").InnerText.ToLower(),
                                          x.SelectSingleNode("/data/data/uuid_short").InnerText.ToLower());
                    }
                }
            }
            //从 ID - UUID 列表获取玩家的 UUID
            string playerUUID = string.Empty;
            playerID = playerID.ToLower();
            if (playerID_UUID.TryGetValue(playerID, out playerUUID) == false) return string.Empty;
            //检查对应的玩家数据文件是否存在，是则返回对应文件名，否则返回空值
            string targetFileName = availableDatePrefix + playerUUID + ".xml";
            if (File.Exists(dataPath + targetFileName)) return targetFileName;
            return string.Empty;
        }

        //将单玩家数据 JSON 文件转换为 XML 文件，并建立 UUID - 玩家ID对照表（注意该方法仅添加，清空由调用方自行提前处理）
        static bool procBuildPlayerToUUID(string dataPath, ref Dictionary<string, string> playerID_UUID)
        {
            WriteCon("开始处理 JSON 并建立玩家 ID - UUID 对查表", true);

            string fName = string.Empty;
            string jsonText; XmlDocument x = new XmlDocument();
            string dataFilePrefix;

            //查找距离今天日期最近的数据文件
            DateTime availableDataDate = DateTime.Now;
            int availableDateTry = 30;
            while (true)
            {
                dataFilePrefix = availableDataDate.ToString("yyyy-MM-dd") + "_";
                if (Directory.GetFiles(dataPath, dataFilePrefix + "*.json").Length > 0) break;
                availableDateTry -= 1; if (availableDateTry == 0) return false;
                availableDataDate = availableDataDate.AddDays(-1);
            }

            WriteCon("最近数据文件日期前缀：" + dataFilePrefix, true);

            foreach (string fPath in Directory.GetFiles(dataPath, dataFilePrefix + "*.json"))
            {
                fName = fPath.Substring(fPath.LastIndexOf("\\") + 1);
                fName = fName.Substring(0, fName.LastIndexOf("."));
                jsonText = File.ReadAllText(fPath, Encoding.UTF8);
                jsonText = jsonText.Replace("minecraft:", "");
                jsonText = jsonText.Replace("/", "_");
                x = JSONtoXML(jsonText);
                if (x.SelectSingleNode("/data/data/playername") == null)
                {
                    WriteCon("处理 " + fName + " 时出错，找不到预期的必要节点", true);
                    WriteCon("已跳过处理 JSON 文件" + fName, true);
                    continue;
                }
                try
                {
                    File.WriteAllText(dataPath + fName + ".xml", x.InnerXml, Encoding.UTF8);
                    if (playerID_UUID.ContainsKey(x.SelectSingleNode("/data/data/playername").InnerText.ToLower()) == false)
                        playerID_UUID.Add(x.SelectSingleNode("/data/data/playername").InnerText.ToLower(),
                                          x.SelectSingleNode("/data/data/uuid_short").InnerText.ToLower());
                }
                catch (Exception ex)
                {
                    WriteCon("保存玩家 XML " + fName + " 时出错：" + ex.Message, true);
                    WriteCon("玩家 " + fName + "对应的数据对外暂时不可用", true);
                }
            }
            if (playerID_UUID.Count > 0) { WriteCon("已成功处理 " + playerID_UUID.Count + " 个玩家数据", true); return true; }
            else { WriteCon("建立玩家 ID-UUID 列表失败，未成功处理任何数据", true); return false; }
        }

        //保存配置文件
        static void SaveSettings(string configFile, string dataPath, IPAddress proxyHost, int proxyPort, string botToken, long msgOffsetID, int tgQueryInterval)
        {
            StringBuilder confBuilder = new StringBuilder();
            confBuilder.AppendLine("<conf>");
            confBuilder.AppendLine("<datapath>" + "<![CDATA[" + dataPath + "]]>" + "</datapath>");
            confBuilder.AppendLine("<proxy>" + proxyHost.ToString() + ":" + proxyPort.ToString() + "</proxy>");
            confBuilder.AppendLine("<bot_token>" + botToken + "</bot_token>");
            confBuilder.AppendLine("<msgOffsetID>" + msgOffsetID.ToString() + "</msgOffsetID>");
            confBuilder.AppendLine("<tgQueryInterval>" + tgQueryInterval.ToString() + "</tgQueryInterval>");
            confBuilder.AppendLine("</conf>");
            try
            {
                File.WriteAllText(configFile, confBuilder.ToString(), Encoding.UTF8);
                WriteCon("配置文件保存成功");
            }
            catch (Exception ex) { WriteCon("保存配置文件时出错：" + ex.Message, true); }
        }

        //获取数据时间（复用方法）
        static string getDataTime(ref XmlDocument x)
        {
            return DateTimeToStr(TimestampMStoDateTime(x.SelectSingleNode("/data/data/lastUpdate").InnerText));
        }

        //读取应答文件
        static string getReplyContent(string name)
        {
            string replyContent;
            string replyFilePath = appPath + @"\data\reply_" + name.ToLower() + ".txt";
            if (File.Exists(replyFilePath))
            {
                replyContent = File.ReadAllText(replyFilePath, Encoding.UTF8);
                return replyContent.Replace("\r\n", "\n");
            }
            else
            {
                WriteCon("找不到应答文件 " + name, true);
                return makeErrorMsgToUser("应答文件不存在");
            }
        }

        //读取成就条件列表
        //其中返回的 Dictionary 的 Key 是 Creteria 下的节点名，Value 为条件的中文显示名称
        static Dictionary<string, string> getAdvCreteriaList(string name)
        {
            Dictionary<string, string> r = new Dictionary<string, string>();
            string listFilePath = appPath + @"\data\advlist_" + name.ToLower() + ".txt";
            if (File.Exists(listFilePath))
            {
                string[] lnParts;
                string[] lns = File.ReadAllLines(listFilePath, Encoding.UTF8);
                foreach (string ln in lns)
                {
                    if (ln.Length < 1) continue;
                    if (ln.Substring(0, 1) == "#") continue;
                    lnParts = ln.Split(',');
                    if (lnParts.Length == 2) { r.Add(lnParts[0], lnParts[1]); }
                    else { WriteCon("分析成就列表 " + name + " 项目 " + ln + "失败", true); }
                }
                return r;
            }
            else
            {
                WriteCon("找不到成就列表文件 " + name, true);
                return r;
            }
        }

        //测试某个XPath指定的节点是否存在
        static bool isNodeAvailable(ref XmlDocument x, string xPath)
        {
            return !(x.SelectSingleNode(xPath) == null);
        }

        //测试某两个XPath指定的节点是否存在，
        static bool isAnyOf2NodeAvailable(ref XmlDocument x, string xPathA, string xPathB)
        {
            return (isNodeAvailable(ref x, xPathA) || isNodeAvailable(ref x, xPathB));
        }

        //测试某个XPath指定的节点、以及附加了前缀的节点是否存在
        static bool isNodeAndwPrefixAvailable(ref XmlDocument x, string upperXPath, string xNodeName, string xNodePrefix)
        {
            if ((x.SelectSingleNode(upperXPath + "/" + xNodeName) != null)
                || (x.SelectSingleNode(upperXPath + "/" + xNodePrefix + xNodeName) != null)) return true;
            return false;
        }

        //检查进度完成情况通用方法
        //程序目录的data文件夹下，必须存在如下文件（进度名为传入的advancement变量内容）：
        //  adv_none_进度名：玩家尚未开始该进度的提示
        //  adv_intro_进度名：玩家正在进行，但尚未完成该进度的提示
        //  adv_done_进度名：玩家已完成该进度的提示
        //以上文件中，必须包含以下占位符：
        //  {player_id} 玩家ID（传入变量：playerID）
        //  {data_time} 数据日期（传入变量：dataTime）
        //  {unfinished_content} 玩家尚未完成某进度时，尚未达到的条件内容

        //检查单一进度完成情况
        //原则上只有一个creteria，可以依赖done字段值判断的单一进度，均可复用该过程
        static void doCheckSingleCretAdvancement(ref XmlDocument x, string advName, string advRootNodeName,
            string playerID, string currentChatID, string botToken, IPAddress proxyHost, int proxyPort)
        {
            //初始化变量
            string msg = string.Empty;
            XmlNode xN;

            //运行开始
            xN = x.SelectSingleNode("/data/advancements/" + advRootNodeName + "/done");
            if (xN == null)
            {
                //玩家尚未开始此进度
                msg = getReplyContent("adv_intro_" + advName);
            }
            else
            {
                //玩家已完成此进度（此时done理应为true，但还是保险一下）
                if (xN.InnerText.ToLower() == "true") { msg = getReplyContent("adv_done_" + advName); }
                else { msg = getReplyContent("adv_intro_" + advName); }
            }
            //统一替换玩家ID、数据时间
            msg = msg.Replace("{player_id}", playerID);
            msg = msg.Replace("{data_time}", getDataTime(ref x));
            tgSendMessage(currentChatID, msg, botToken, proxyHost, proxyPort);
        }

        //检查复合进度完成情况
        //除特殊情况外（如因生物群系名称发生大变动，而无法妥协的『探索的时光』进度），其它进度完成情况理论上均可复用该过程
        //支持单条件（条件,中文名称）和多对一（条件1|条件2|...,中文名称）条件判断
        static void doCheckMultiCretAdvancement(ref XmlDocument x, string advName, string advNameCN, string advRootNodeName,
            string playerID, string currentChatID, string botToken, IPAddress proxyHost, int proxyPort)
        {
            //初始化变量
            string msg = string.Empty;
            XmlNode xN;
            Dictionary<string, string> advCreterias;
            string creteriaRoot;
            bool hasUnfinishedAdv;
            string[] multiCret; bool cretResult;

            //运行开始
            advName = advName.ToLower();
            xN = x.SelectSingleNode("/data/advancements/" + advRootNodeName);
            if (xN == null)
            {
                msg = getReplyContent("adv_none_" + advName);
            }
            else
            {
                msg = getReplyContent("adv_intro_" + advName);
                advCreterias = getAdvCreteriaList(advName);
                creteriaRoot = "/data/advancements/" + advRootNodeName + "/criteria";
                StringBuilder lns = new StringBuilder();
                hasUnfinishedAdv = false;

                //检查进度完成情况
                if (advCreterias.Count > 0)
                {
                    //初始化
                    foreach (KeyValuePair<string, string> advCret in advCreterias)
                    {
                        //重置判断结果
                        cretResult = false;
                        //含有|的是多条件判断
                        if (advCret.Key.Contains("|"))
                        {
                            //多条件，拆分后做 OR 判断
                            multiCret = advCret.Key.Split('|');
                            foreach (string multiCretItem in multiCret)
                            {
                                if (multiCretItem.Length > 0)
                                {
                                    //多条件只需要满足一个即可
                                    if (isNodeAvailable(ref x, creteriaRoot + "/" + multiCretItem) == true)
                                    { cretResult = true; break; }
                                }
                            }
                            //注意在上述过程中，如果满足任一条件
                            //输出变量 cretResult 的值即已为 TRUE
                        }
                        else
                        {
                            //单条件
                            if (isNodeAvailable(ref x, creteriaRoot + "/" + advCret.Key) == true)
                                cretResult = true;
                        }
                        //最终判断
                        if (cretResult == false)
                        { hasUnfinishedAdv = true; lns.AppendLine("> " + advCret.Value); }
                    }
                    //如果有未完成进度，替换占位符，否则重新载入已完成信息模板
                    if (hasUnfinishedAdv == true) { msg = msg.Replace("{unfinished_content}", lns.ToString()); }
                    else { msg = getReplyContent("adv_done_" + advName); }
                }
                else
                {
                    tgSendMessage(currentChatID, makeErrorMsgToUser("读取『" + advNameCN + "』成就列表时发生错误"), botToken, proxyHost, proxyPort);
                    return;
                }
            }
            //统一替换玩家ID、数据时间
            msg = msg.Replace("{player_id}", playerID);
            msg = msg.Replace("{data_time}", getDataTime(ref x));
            tgSendMessage(currentChatID, msg, botToken, proxyHost, proxyPort);
        }

        //判断数据版本，包含/data/stats_source/stats节点的为 1.13.2 后的新数据（其下有行为子节点），否则为 1.12 时期的旧数据
        static dataVersion getDataVersion(ref XmlDocument x)
        {
            if (isNodeAvailable(ref x, "/data/stats_source/stats")) return dataVersion.MC1132;
            return dataVersion.MC112;
        }

        //读取节点数据并以 long 类型返回
        //注意由于本应用调用数据的特点（不可能有负数，只有0和正整数），当节点不存在或数据异常（无法转换为long）时，返回 -1。
        static long getNodeValueInLong(ref XmlDocument x, string xPath)
        {
            XmlNode xN = x.SelectSingleNode(xPath);
            if (xN == null) return -1;
            long r;
            return long.TryParse(xN.InnerText, out r) ? r : -1;
        }

        //加载物品ID-名称对应表
        static Dictionary<string, string> getItemIDtoNameList()
        {
            Dictionary<string, string> r = new Dictionary<string, string>();
            string listFilePath = appPath + @"\data\item_id_list.txt";
            string[] lnParts;
            if (File.Exists(listFilePath))
            {
                string[] lstLines = File.ReadAllLines(listFilePath, Encoding.UTF8);
                int lnNum = 0;
                foreach (string ln in lstLines)
                {
                    lnNum = lnNum + 1;
                    if (ln.Length < 1) continue;                //空行
                    if (ln.Trim().Length < 1) continue;         //去掉空格还是空行
                    if (ln.Substring(0, 1) == "#") continue;    //# 开头的为注释
                    if (ln.Contains(",") == false) continue;    //行内必须有西文逗号，否则无效
                    lnParts = ln.Split(',');
                    if (lnParts.Length != 2) continue;          //拆分后必须为 2 部分，否则无效
                    if (lnParts[0].Length < 1 || lnParts[1].Length < 1) continue; //拆分后的 2 部分必须都有内容
                    //不允许出现重复的物品 ID，该问题需要在外部处理完毕
                    if (r.ContainsKey(lnParts[0]))
                    {
                        WriteCon("第 " + lnNum + " 行发现重复的物品 ID：" + lnParts[0]);
                        WriteCon("请修正该问题后重新启动服务端");
                        r.Clear(); break;
                    }
                    r.Add(lnParts[0], lnParts[1]);
                }
            }
            return r;
        }

        //清洗用户输入的玩家/方块/物品 ID
        static string sanitizeID(string s, ref List<string> idAllowedChar)
        {
            string input = s.ToLower().Trim();
            StringBuilder r = new StringBuilder();
            for (int n = 0; n < s.Length; n++) { if (idAllowedChar.Contains(s.Substring(n, 1))) r.Append(s.Substring(n, 1)); }
            return r.ToString();
        }

        //读取随机词条文件，并返回其中的一行
        static string getRandomDictLine(string name)
        {
            string[] randomDict; Random rnd = new Random();
            string randomDictPath = appPath + @"\data\random_" + name.ToLower() + ".txt";
            if (File.Exists(randomDictPath))
            {
                randomDict = File.ReadAllLines(randomDictPath, Encoding.UTF8);
                return randomDict[rnd.Next(0, randomDict.Length - 1)];
            }
            else
            {
                WriteCon("找不到随机词条文件 " + name, true);
                return makeErrorMsgToUser("随机词条文件不存在");
            }
        }

        /* - - - - - - - - - - 基础辅助方法开始 - - - - - - - - - - */

        //按任意键退出
        static void AnyKeyExit() { WriteCon("按任意键退出..."); Console.ReadKey(true); }

        //写控制台
        static void WriteCon(string s)
        {
            Console.WriteLine("[{0}] {1}", DateTime.Now.ToString(), s);
        }

        //写控制台（只回车不换行）
        static void WriteConAndCR(string s)
        {
            Console.Write("[{0}] {1}", DateTime.Now.ToString(), s + "\r");
        }

        //写控制台的同时写日志
        static void WriteCon(string s, bool writeLogAsWell)
        {
            WriteCon(s); WriteLog(s);
        }

        //写日志文件
        static void WriteLog(string s)
        {
            try { File.AppendAllText(logFile, "\r\n" + "[" + DateTime.Now.ToString() + "]" + " " + s); }
            catch (Exception ex) { WriteCon("写入日志时发生错误：" + ex.Message); }
        }

        //转换精确到ms的时间戳为DateTime
        static DateTime TimestampMStoDateTime(string t)
        {
            long tAddSec = long.Parse(t.Substring(0, t.Length - 3));
            DateTime ts = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            ts = ts.AddSeconds(tAddSec).AddHours(8);
            return ts;
        }

        //转换DateTime为精确到ms的时间戳（注意实际没有精确到ms，只是补了3个0）
        static long DateTimeToTimeStampMS(DateTime d)
        {
            TimeSpan ts = d - new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddHours(8);
            return (long)ts.TotalSeconds * 1000;
        }

        //转换DateTime为文本形式
        static string DateTimeToStr(DateTime d)
        {
            return d.ToString("yyyy-MM-dd HH:mm:ss");
        }

        //转换秒数为多少个小时
        static string SecToHour(string t)
        {
            double dSec = double.Parse(t);
            TimeSpan ts = TimeSpan.FromSeconds(dSec);
            return (int)ts.TotalHours + " 小时";
        }

        //使用 Newtonsoft.Json 库将 JSON 转换为 XML
        static XmlDocument JSONtoXML(string json)
        {
            try { return JsonConvert.DeserializeXmlNode(json, "data"); }
            catch (Newtonsoft.Json.JsonSerializationException)
            {
                try { return JsonConvert.DeserializeXmlNode("{\"jsonArray\":" + json + "}", "root"); }
                catch { return new XmlDocument(); }
            }
            catch { return new XmlDocument(); }
        }

        //发送 GET 请求
        static string httpGET(string url, string queryString, IPAddress proxyHost, int proxyPort, bool allowLongPoll)
        {
            Debug.WriteLine("HTTP GET: " + url);
            if (string.IsNullOrEmpty(queryString) == false)
            {
                string[] queryStrKVP;
                string[] queryStrs = queryString.Split('&');
                StringBuilder encodedQueryStr = new StringBuilder();
                foreach (string queryStr in queryStrs)
                {
                    queryStrKVP = queryStr.Split('=');
                    if (queryStrKVP.Length != 2) continue;
                    encodedQueryStr.Append("&" + queryStrKVP[0] + "=" + HttpUtility.UrlEncode(queryStrKVP[1]));
                }
                url = url + "?" + encodedQueryStr.ToString().Substring(1);
            }
            HttpWebRequest wReq = (HttpWebRequest)HttpWebRequest.Create(url);
            wReq.Method = "GET";
            wReq.ContentType = "application/x-www-form-urlencoded";
            if (allowLongPoll == true)
            {
                wReq.Timeout = 60 * 1000;
                wReq.ReadWriteTimeout = 60 * 1000;
            }
            else
            {
                wReq.Timeout = 5 * 1000;
                wReq.ReadWriteTimeout = 5 * 1000;
            }
            wReq.Proxy = new WebProxy(proxyHost.ToString(), proxyPort);
            //发起请求并返回结果
            try
            {
                Debug.WriteLine("GET " + url);
                WebResponse wResp = wReq.GetResponse();
                string r = new StreamReader(wResp.GetResponseStream(), Encoding.UTF8).ReadToEnd();
                wResp.Close();
                Debug.WriteLine("GET return: " + r);
                return r;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    string r = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8).ReadToEnd();
                    wex.Response.Close();
                    Debug.WriteLine("GET Error: " + r);
                    return r;
                }
                else
                {
                    Debug.WriteLine("GET Error, empty response");
                    return string.Empty;
                }
            }
            catch(Exception ex)
            {
                return string.Empty;
            }
        }

        //发送POST请求
        static string httpPOST(string url, string postData, IPAddress proxyHost, int proxyPort)
        {
            HttpWebRequest wReq = (HttpWebRequest)HttpWebRequest.Create(url);
            wReq.Method = "POST";
            wReq.ContentType = "application/x-www-form-urlencoded";
            wReq.Timeout = 5000;
            wReq.ReadWriteTimeout = 5000;
            wReq.Proxy = new WebProxy(proxyHost.ToString(), proxyPort);
            wReq.ServicePoint.Expect100Continue = false;
            //写 POST 数据
            try
            {
                byte[] postBytes = Encoding.UTF8.GetBytes(postData);
                wReq.ContentLength = postBytes.Length;
                Stream postStream = wReq.GetRequestStream();
                postStream.Write(postBytes, 0, postBytes.Length);
                postStream.Close();
                postBytes = null;
            }
            catch
            {
                return "error";
            }
            //发起请求并返回结果
            try
            {
                Debug.WriteLine("POST " + url);
                Debug.WriteLine("POST data: " + postData);
                WebResponse wResp = wReq.GetResponse();
                string r = new StreamReader(wResp.GetResponseStream(), Encoding.UTF8).ReadToEnd();
                wResp.Close();
                Debug.WriteLine("POST return: " + r);
                return r;
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    string r = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8).ReadToEnd();
                    wex.Response.Close();
                    Debug.WriteLine("POST Error: " + r);
                    return r;
                }
                else
                {
                    Debug.WriteLine("POST Errror, empty response");
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                return string.Empty;
            }
        }
    }
}