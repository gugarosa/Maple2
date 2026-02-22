// ReSharper disable InconsistentNaming

using System.Globalization;
using System.Reflection;
using Maple2.Model.Enum;

#pragma warning disable IDE1006 // Naming Styles


namespace Maple2.Model.Metadata;

public static class Constant {
    /// <summary>
    /// Initialize XML-sourced constants from parsed constants tables.
    /// Called during server startup after metadata is loaded.
    /// </summary>
    public static void Initialize(ConstantsTable? table, ServerConstantsTable? serverTable) {
        if (table != null) {
            ApplyConstants(table.Entries);
        }
        if (serverTable != null) {
            ApplyConstants(serverTable.Entries);
        }
    }

    private static void ApplyConstants(IReadOnlyDictionary<string, string> entries) {
        foreach ((string key, string value) in entries) {
            FieldInfo? field = typeof(Constant).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (field == null || field.IsInitOnly || field.IsLiteral) {
                continue;
            }

            try {
                object? parsed = ParseValue(field.FieldType, value);
                if (parsed != null) {
                    field.SetValue(null, parsed);
                }
            } catch {
                // Skip values that fail to parse
            }
        }
    }

    private static object? ParseValue(Type type, string value) {
        // Strip trailing 'f' suffix common in XML values (e.g. "500.0f")
        string cleaned = value.TrimEnd('f', 'F');

        if (type == typeof(int)) return int.Parse(cleaned, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return float.Parse(cleaned, CultureInfo.InvariantCulture);
        if (type == typeof(byte)) return byte.Parse(cleaned, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return cleaned != "0" && !string.Equals(cleaned, "false", StringComparison.OrdinalIgnoreCase);
        if (type == typeof(string)) return value;

        return null;
    }

    #region custom constants
    public const int ServerMaxCharacters = 8;
    public const int CharacterNameLengthMax = 12;
    public const long MaxMeret = long.MaxValue;
    public const long MaxMeso = long.MaxValue;
    public const long StarPointMax = 999999;
    public const long MesoTokenMax = 100000;
    public const int MaxSkillTabCount = 3;
    public const int BuddyMessageLengthMax = 25;
    public const int MaxBuddyCount = 100;
    public const int MaxBlockCount = 100;
    public const int GemstoneGrade = 4;
    public const int LapenshardGrade = 3;
    public const int InventoryExpandRowCount = 6;
    public const int DefaultReturnMapId = 2000062; // Lith Harbor
    public const int DefaultHomeMapId = 62000000; // Private Residence
    public const int DefaultHomeNumber = 1;
    public const byte MinHomeArea = 4;
    public const byte MaxHomeArea = 25;
    public const byte MinHomeHeight = 3;
    public const byte MaxHomeHeight = 15;
    public const short FurnishingStorageMaxSlot = 1024;
    public const int ConstructionCubeItemId = 50200183;
    public const int HomeNameMaxLength = 16;
    public const int HomeMessageMaxLength = 100;
    public const int HomePasscodeLength = 6;
    public const int HomeMaxLayoutSlots = 5;
    public const int PerformanceMapId = 2000064; // Queenstown
    public static readonly TimeSpan MaxPerformanceDuration = TimeSpan.FromMinutes(10);
    public const int BaseStorageCount = 36;
    public const float MesoMarketTaxRate = 0.1f;
    public const float MesoMarketRangeRate = 0.2f;
    public const int MesoMarketSellEndDay = 2;
    public const int MesoMarketListLimit = 5;
    public const int MesoMarketListLimitDay = 5;
    public const int MesoMarketPurchaseLimitMonth = 30;
    public const int MesoMarketPageSize = 50;
    public const int MesoMarketMinToken = 100;
    public const int MesoMarketMaxToken = 1000;
    public const int FishingMasteryMax = 2990;
    public const int PerformanceMasteryMax = 10800;
    public const int MiningMasteryMax = 81440;
    public const int ForagingMasteryMax = 81440;
    public const int RanchingMasteryMax = 81440;
    public const int FarmingMasteryMax = 81440;
    public const int SmithingMasteryMax = 81440;
    public const int HandicraftsMasteryMax = 81440;
    public const int AlchemyMasteryMax = 81440;
    public const int CookingMasteryMax = 81440;
    public const int PetTamingMasteryMax = 100000;
    public const int ChangeAttributesMinLevel = 50;
    public const int ChangeAttributesMinRarity = 4;
    public const int ChangeAttributesMaxRarity = 6;
    public const double FishingMasteryAdditionalExpChance = 0.05;
    public const int FishingMasteryIncreaseFactor = 2;
    public const int FishingRewardsMaxCount = 1;
    public const double FishingItemChance = 0.03;
    public const float FishingBigFishExpModifier = 1.5f;
    public const int MaxMottoLength = 20;
    public const ItemTag BeautyHairSpecialVoucherTag = ItemTag.beauty_hair_special;
    public const ItemTag BeautyHairStandardVoucherTag = ItemTag.beauty_hair;
    public const ItemTag BeautyFaceVoucherTag = ItemTag.beauty_face;
    public const ItemTag BeautyMakeupVoucherTag = ItemTag.beauty_makeup;
    public const ItemTag BeautySkinVoucherTag = ItemTag.beauty_skin;
    public const ItemTag BeautyItemColorVoucherTag = ItemTag.beauty_itemcolor;
    public const int HairPaletteId = 2;
    public const int MaxBuyBackItems = 12;
    public const DayOfWeek ResetDay = DayOfWeek.Thursday;
    public const int PartyMaxCapacity = 10;
    public const int PartyMinCapacity = 4;
    public const int GroupChatMaxCapacity = 20;
    public const int GroupChatMaxCount = 3;
    public const long ClientGraceTimeTick = 500; // max time to allow client to go past loop & sequence end
    public const long MaxNpcControlDelay = 500;
    public const float BlackMarketPremiumClubDiscount = 0.2f;
    public const double PetAttackMultiplier = 0.394;
    public const double AttackDamageFactor = 4; // Unconfirmed
    public const double CriticalConstant = 5.3;
    public const double CriticalPercentageConversion = 0.015;
    public const double MaxCriticalRate = 0.4;
    public const int MaxClubMembers = 10;
    public const string PetFieldAiPath = "Pet/AI_DefaultPetTaming.xml";
    public const string DefaultAiPath = "AI_Default.xml";
    public const int GuildCoinId = 30000861;
    public const int GuildCoinRarity = 4;
    public const int BlueprintId = 35200000;
    public const int EmpowermentNpc = 11003416;
    public const int OpheliaNpc = 11000508;
    public const int PeachyNpc = 11000510;
    public const int InteriorPortalCubeId = 50400158;
    public const int PortalEntryId = 50400190;
    public const int Grade1WeddingCouponItemId = 20303166;
    public const int Grade2WeddingCouponItemId = 20303167;
    public const int Grade3WeddingCouponItemId = 20303168;
    public const int MinStatIntervalTick = 100;
    public const int HomePollMaxCount = 5;

    public static readonly TimeSpan WorldBossIdleWarningThreshold = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan WorldBossDespawnThreshold = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WorldBossMonitorInterval = TimeSpan.FromSeconds(30);

    public const int MaxMentees = 3;

    public const long FurnishingBaseId = 2870000000000000000;
    public const bool AllowWaterOnGround = false;

    public const int HomeDecorationMaxLevel = 10;

    public const bool EnableRollEverywhere = false;
    public const bool HideHomeCommands = true;

    public const int MaxAllowedLatency = 2000;

    public const bool DebugTriggers = false; // Set to true to enable debug triggers. (It'll write triggers to files and load triggers from files instead of DB)

    public const bool AllowUnicodeInNames = false; // Allow Unicode characters in character and guild names

    public static IReadOnlyDictionary<string, int> ContentRewards { get; } = new Dictionary<string, int> {
        { "miniGame", 1005 },
        { "dungeonHelper", 1006 },
        { "MiniGameType2", 1007 }, // Shanghai Runners
        { "UserOpenMiniGameExtraReward", 1008 }, // Player hosted mini game extra rewards
        { "PrestigeRankUp", 1020 },
        { "NormalHardDungeonBonusTier1", 10000001 },
        { "NormalHardDungeonBonusTier2", 10000002 },
        { "NormalHardDungeonBonusTier3", 10000003 },
        { "NormalHardDungeonBonusTier4", 10000004 },
        { "NormalHardDungeonBonusTier5", 10000005 },
        { "NormalHardDungeonBonusTier6", 10000006 },
        { "NormalHardDungeonBonusTier7", 10000007 },
        { "NormalHardDungeonBonusTier8", 10000008 },
        { "QueenBeanArenaRound1Reward", 10000009 },
        { "QueenBeanArenaRound2Reward", 10000010 },
        { "QueenBeanArenaRound3Reward", 10000011 },
        { "QueenBeanArenaRound4Reward", 10000012 },
        { "QueenBeanArenaRound5Reward", 10000013 },
        { "QueenBeanArenaRound6Reward", 10000014 },
        { "QueenBeanArenaRound7Reward", 10000015 },
        { "QueenBeanArenaRound8Reward", 10000016 },
        { "QueenBeanArenaRound9Reward", 10000017 },
        { "QueenBeanArenaRound10Reward", 10000018 },
    };

    public const bool MailQuestItems = false; // Mail quest item rewards if inventory is full

    #region Field
    public static readonly TimeSpan FieldUgcBannerRemoveAfter = TimeSpan.FromHours(4);
    public static readonly TimeSpan FieldDisposeLoopInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FieldDisposeEmptyTime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DungeonDisposeEmptyTime = TimeSpan.FromMinutes(5);
    #endregion

    #region Character
    public static readonly int[] DefaultEmotes = [
        90200011, // Greet
        90200004, // Scheme
        90200024, // Reject
        90200041, // Sit
        90200042, // Ledge Sit
        90200057, // Possessed Fan Dance
        90200043, // Epiphany
        90200022, // Bow
        90200031, // Cry
        90200005, // Dejected
        90200006, // Like
        90200003, // Pout
        90200092, // High Five
        90200077, // Catch of the Day
        90200073, // Make It Rain
        90200023, // Surprise
        90200001, // Anger
        90200019, // Scissors
        90200020, // Rock
        90200021, // Paper
    ];
    #endregion

    #region Account
    public static readonly bool AutoRegister = true;
    public static readonly bool BlockLoginWithMismatchedMachineId = false;
    public static readonly int DefaultMaxCharacters = 4;
    #endregion
    #endregion

    #region client constants
    public const int MaxClosetMaxCount = 5;
    public const int MaxClosetTabNameLength = 10;
    public const int WeddingProposeItemID = 11600482;
    public const int WeddingInvitationMaxCount = 70;
    public const int WeddingProposeCooltime = 2;
    public const int WeddingDivorceFieldID = 84000002;
    public const int WeddingInvitationMeso = 1000;
    public const int WeddingDivorceMeso = 1000000;
    public const int WeddingCoolingOffDay = 7;
    public const int WeddingPromiseLimitDay = 7;
    public const int WeddingHallModifyLimitHour = 3;
    public const int WeddingDivorceRequireMarriageDay = 30;
    public const int CharacterNameLengthMin = 2;
    public const int BlockSize = 150;
    public const float SouthEast = 0;
    public const float NorthEast = 90;
    public const float NorthWest = -180;
    public const float SouthWest = -90;
    public const short HairSlotCount = 30;
    public const ShopCurrencyType InitialTierExcessRestockCurrency = ShopCurrencyType.Meso;
    public const float UGCShopProfitFee = 0.25f;
    public const int UGCShopProfitDelayDays = 10;
    public const int PartyFinderListingsPageCount = 12;
    public const int ProposalItemId = 11600482;
    #endregion

    #region table/constants.xml
    public static float NPCColorScale = 2.0f;
    public static float NPCDuration = 0.2f;
    public static float PCColorScale = 2.0f;
    public static float PCDuration = 0.2f;
    public static float GetEXPColorScale = 0.5f;
    public static float GetEXPDuration = 0.2f;
    public static float AccumulationRatio = 0.1f;
    public static float NPCCliffHeight = 50.0f;
    public static float NPCRandomDeadPushRate = 0.2f;
    public static float CustomizingRotationSpeed = 75.0f;
    public static float CustomizingWheelSpeed_Morph = 0.1f;
    public static float CustomizingWheelSpeed_Item = 0.1f;
    public static float CustomizingWheelSpeed_Makeup = 0.1f;
    public static float CustomizingRotationSpeed_Makeup = 1.0f;
    public static float CustomizingHairFirstPartHighlight = 0.1f;
    public static float CustomizingHairSecondPartHighlight = 1.0f;
    public static float LookAtInterval = 15.0f;
    public static float LookAtDistanceNPC = 500.0f;
    public static float LookAtDistanceCry = 500.0f;
    public static bool EnableSkillJumpDown = true;
    public static bool EscapeHitMethodSkill = false;
    public static bool EscapeHitMethodJump = false;
    public static bool EscapeHitMethodMove = false;
    public static bool EscapeHitMoveKeyIsDown = false;
    public static bool AllowComboAtComboPoint = true;
    public static bool CancelSwing_KeyIsDown = true;
    public static bool SkillGlobalCooldown = false;
    public static bool SkillGlobalCooldown_CheckSameSkill = true;
    public static int AttackRotationSpeed = 90;
    public static int ChaosModeTime = 20;
    public static int ChaosPointPerBlock = 20;
    public static int ChaosPointMaxBlock = 1;
    public static int ChaosPointGetLevel0 = 1;
    public static int ChaosPointGetPoint0 = 120;
    public static int ChaosActionGetLevel0 = 15;
    public static int ChaosActionGetLevel1 = 25;
    public static int ChaosActionGetLevel2 = 55;
    public static int ChaosActionGetLevel3 = 95;
    public static int ChaosActionGetLevel4 = 145;
    public static int OnEnterTriggerClientSideOnlyTick = 100;
    public static int OnEnterTriggerDefaultTick = 1000;
    public static int TalkTimeover = 60000;
    public static int DamageDistance = 2500;
    public static int TalkableDistance = 150;
    public static bool TalkableFrontOnly = true;
    public static int DropIconVisibleDistance = 400;
    public static int ChatBalloonDistance = 2000;
    public static int HpBarDistance = 9999999;
    public static int EmoticonVisibleDistance = 2500;
    public static int RegisterUgcDistance = 150;
    public static int RegisterUgcDistanceClose = 300;
    public static int ConstructUgcDistance = 150;
    public static int FunctionCubeDistance = 125;
    public static int InteractionDistance = 155;
    public static int HouseMarkShowDistance = 2000;
    public static int HouseMarkShowClippingUp = 1000;
    public static int HouseMarkShowClippingDown = 500;
    public static int HouseMarkPopupDistance = 160;
    public static int UgcBoundaryStartDistance = 1;
    public static int UgcBoundaryEndDistance = 7;
    public static int DurationForBoundaryDisplay = 3000;
    public static TimeSpan UgcHomeSaleWaitingTime = TimeSpan.FromSeconds(259200);
    public static int UgcContainerExpireDurationNormal = 90;
    public static int UgcContainerExpireDurationCash = 365;
    public static int UgcContainerExpireDurationMeret = 365;
    public static int UgcHomeExtensionNoticeDate = 30;
    public static int UgcHomePasswordExpireDuration = 86400;
    public static bool CubeLiftHeightLimitUp = true;
    public static bool CubeLiftHeightLimitDown = true;
    public static int CubeCraftSafetyCapID = 11300053;
    public static int CubeCraftLightStickLeftID = 13100014;
    public static int CubeCraftLightStickRightID = 13100046;
    public static float DropIconDistance = 200.0f;
    public static int DropIconHeadOffset = 40;
    public static int DropItemMaxLength = 300;
    public static int DropMoneyMaxLength = 300;
    public static float DropItemTargetZPos = 200.0f;
    public static float DropItemPickUpVel = 200.0f;
    public static float DropItemPickUpGravity = -38.0f;
    public static float DropItemPickUpCompleteRotateTime = 0.1f;
    public static int DropItemPickUpCompleteRotateVel = 5;
    public static int DropMoneyActiveProbability = 0;
    public static int DropMoneyProbability = 0;
    public static int ChatBalloonDuration = 5000;
    public static int BoreWaitingTick = 50000;
    public static int OffsetPcHpBar = 32;
    public static int OffsetPcNameTag = 30;
    public static int OffsetPcChatBalloon = -30;
    public static int OffsetPcDamageNumber = 0;
    public static int OffsetPcMissionIndicator = 20;
    public static int OffsetPcProfileTag = 0;
    public static float fOffsetOnTombstoneNameTag = -5.0f;
    public static int OffsetNpcHpBar = 5;
    public static int OffsetNpcNameTag = 5;
    public static int OffsetNpcEmoticon = -30;
    public static int OffsetNpcChatBalloon = -30;
    public static int OffsetNpcDamageNumber = 0;
    public static int OffsetNpcMonologue = 40;
    public static int OffsetActionTooltipX = 70;
    public static int OffsetActionTooltipY = -40;
    public static int OffsetPcPopupMenu = 60;
    public static int DamageGap = 30;
    public static int DamageRenderCount = 3;
    public static int DamageRenderTotalCount = 25;
    public static float DamageOtherScale = 0.5f;
    public static float DamageOtherAlpha = 0.3f;
    public static int DamageEffectMinHPPercent = 30;
    public static int DamageEffectCriticalPercent = 10;
    public static int questHideTime = 30;
    public static int questIntervalTime = 60;
    public static int ShopResetChance = 10;
    public static int ShopSeedResetTime = 60;
    public static int ShopRepurchaseMax = 12;
    public static int ShopSellConfirmPrice = 10000;
    public static int ShopBuyConfirmPrice = 0;
    public static int DashKeyInputDelay = 500;
    public static int DashSwimConsumeSP = 20;
    public static int DashSwimMoveVel = 2;
    public static float Glide_Gravity = 0.0f;
    public static float Glide_Height_Limit = 0.0f;
    public static float Glide_Horizontal_Accelerate = 0.0f;
    public static int Glide_Horizontal_Velocity = 500;
    public static float Glide_Vertical_Accelerate = 0.0f;
    public static int Glide_Vertical_Velocity = 150;
    public static int Glide_Vertical_Vibrate_Amplitude = 300;
    public static float Glide_Vertical_Vibrate_Frequency = 1500.0f;
    public static bool Glide_Effect = true;
    public static string Glide_Effect_Run = "CH/Common/Eff_Fly_Balloon_Run.xml";
    public static string Glide_Effect_Idle = "CH/Common/Eff_Fly_Balloon_Idle.xml";
    public static string Glide_Ani_Idle = "Fly_Idle_A";
    public static string Glide_Ani_Left = "Gliding_Left_A";
    public static string Glide_Ani_Right = "Gliding_Right_A";
    public static string Glide_Ani_Run = "Fly_Run_A";
    public static float ClimbVelocityV = 3.0f;
    public static float ClimbVelocityH = 1.5f;
    public static int StoreExpandMaxSlotCount = 144;
    public static int StoreExpandPrice1Row = 330;
    public static int StoreDepositMax = 2000000000;
    public static int StoreWithdrawMax = 2000000000;
    public static int CameraExtraMoveScaleByMonster = 3;
    public static int CameraExtraMoveScaleByMap = 2;
    public static int CameraExtraDistance = 200;
    public static float CameraFinalLoose = 0.08f;
    public static float CameraCurrentLoose = 0.002f;
    public static float CameraUpdateLoose = 0.03f;
    public static int CameraVelocityInPortalMove = 6000;
    public static int ConsumeCritical = 5;
    public static int MonologueInterval = 15;
    public static int MonologueRandom = 10;
    public static int MonologueShowTime = 5;
    public static int ShowKillCountMin = 3;
    public static int UserRevivalInvincibleTick = 5000;
    public static int UserRevivalPenaltyPercent = 15;
    public static string UserRevivalIconPath = "./data/resource/image/skill/icon/deathPenalty.png";
    public static string UserRevivalInvincibleIconPath = "./data/resource/image/skill/icon/deathInvincible.png";
    public static int GetExpMinVelocity = 250;
    public static int GetExpVelocityPer1Length = 2;
    public static string GetExpControlValue0 = "-0.5,0,0.25";
    public static string GetExpControlValue1 = "0.5,-0.25,0.5";
    public static string GetExpTargetPCDummyName = "Eff_Body";
    public static float GetExpTimeAcceleration = 1.02f;
    public static float GetExpCollisionRadius = 15.0f;
    public static int DayToNightTime = 10000;
    public static float MyPCDayTiming = 0.5f;
    public static float MyPCNightTiming = 0.5f;
    public static float BGMTiming = 0.5f;
    public static int dayBaseMinute = 1;
    public static int dayMinute = 1439;
    public static int nightMinute = 1;
    public static int SkipFrameGameObject = 5;
    public static int SkipFrameDistanceGameObject = 2000;
    public static float RegionSkillFadeOutDuration = 0.3f;
    public static int PassengerProfileImageSize = 50;
    public static int PassengerProfileImageLifeTime = 3;
    public static int PassengerProfileImageShowNumber = 3;
    public static int PassengerProfileImageShowCooldown = 57;
    public static int PassengerProfileImageShowCooldownParty = 57;
    public static int PassengerProfileImageShowRange = 400;
    public static int QuestRewardSkillSlotQuestID1 = 1010002;
    public static int QuestRewardSkillSlotQuestID2 = 1010003;
    public static int QuestRewardSkillSlotQuestID3 = 1010004;
    public static int QuestRewardSkillSlotQuestID4 = 1010005;
    public static int QuestRewardSkillSlotQuestID5 = 1010010;
    public static int QuestRewardSkillSlotItemID1 = 40000000;
    public static int QuestRewardSkillSlotItemID2 = 40200001;
    public static int QuestRewardSkillSlotItemID3 = 20000001;
    public static int QuestRewardSkillSlotItemID4 = 40000055;
    public static int QuestRewardSkillSlotItemID5 = 40000056;
    public static int UGCCameraDefaultSize = 320;
    public static int UGCCameraMinSize = 160;
    public static int UGCCameraMaxSize = 640;
    public static int UGCCameraSnapshotPreviewTime = 3000;
    public static int UGCImgUploadSizeLimit = 1024;
    public static int UGCImgFileCountCheck = 200;
    public static int WindAmp2Cloak = 1500;
    public static float WindPeriod2Cloak = 0.7f;
    public static float WindPeriodVar2Cloak = 0.4f;
    public static int autoTargetingMaxDegree = 210;
    public static float VolumeMyPcToNpc = 1.0f;
    public static float VolumeMyPcToObject = 0.5f;
    public static float VolumeMyPcToBreakableObject = 0.8f;
    public static float VolumeNpcToMyPc = 0.7f;
    public static float VolumePcToNpc = 0.3f;
    public static float VolumePcToBreakableObject = 0.3f;
    public static float VolumeNpcToPc = 0.5f;
    public static float VolumeOtherPc = 0.9f;
    public static int ItemDropLevelMaxBoundary = 1;
    public static float moneyTreeDropHeight = 300.0f;
    public static float moneyTreeDropBase = 150.0f;
    public static int moneyTreeDropRandom = 200;
    public static int WhisperIgnoreTime = 1000;
    public static int WhisperMaxCount = 3;
    public static int WhisperDurationTime = 3000;
    public static float BossHitVibrateFreq = 10.0f;
    public static float BossHitVibrateAmp = 5.5f;
    public static float BossHitVibrateDamping = 0.7f;
    public static float BossHitVibrateDuration = 0.1f;
    public static float BossHpBarAutoDetectRange = 1500.0f;
    public static float BossHpBarDuration = 5.0f;
    public static float FindHoldTargetRange = 230.0f;
    public static int FindGrabNodeRange = 2000;
    public static string UgcShopCharCameraLookat = "0,0,70";
    public static string UgcShopCharCameraPos = "220,0,0";
    public static int UgcShopCharCameraMinDistance = 150;
    public static int UgcShopCharCameraZoomVelocity = 700;
    public static string UgcShopCubeCameraLookat = "0,0,80";
    public static string UgcShopCubeCameraPos = "420,0,350";
    public static int UgcShopCubeCameraMinDistance = 450;
    public static int UgcShopCubeCameraZoomVelocity = 700;
    public static string UgcShopRideeCameraLookat = "10,-5,50";
    public static string UgcShopRideeCameraPos = "275,0,150";
    public static int UgcShopRideeCameraMinDistance = 250;
    public static int UgcShopRideeCameraZoomVelocity = 700;
    public static int FieldCachingCount = 2;
    public static float FieldCachingTime = 300.0f;
    public static int FieldCachingMaxCount = 4;
    public static int FieldUnloadThreshold = 10;
    public static float EffectLODOneStepDistance = 450.0f;
    public static float EffectLODTwoStepDistance = 500.0f;
    public static float EffectLODThreeStepDistance = 550.0f;
    public static int TelescopeFindDistance = 200;
    public static int BoatPrice = 500;
    public static int QuestGuidePageCount = 3;
    public static int QuestGuideMaxCount = 60;
    public static float CameraInterpolationTime = 0.4f;
    public static int OneTimeWeaponItemID = 15000001;
    public static int TransparencyCP = 11399999;
    public static int TransparencyEY = 11199999;
    public static int TransparencyCL = 11499999;
    public static int TransparencyPA = 11599999;
    public static int TransparencyMT = 11899999;
    public static int TransparencyEA = 11299999;
    public static int TransparencyFH = 11099999;
    public static int TransparencyGL = 11699999;
    public static int TransparencyRI = 12099999;
    public static int TransparencySH = 11799999;
    public static float DefaultDropItemAlpha = 0.3f;
    public static float DropItemPickFailHeight = 50.0f;
    public static float DropItemPickFailTime = 0.3f;
    public static int TaxiStationFindDistance = 200;
    public static int TaxiCallDuration = 3000;
    public static int TaxiCallBestDriverDuration = 1000;
    public static int TaxiCallBestDriverLevel = 25;
    public static int AirTaxiCashCallDuration = 500;
    public static int AirTaxiMesoCallDuration = 3000;
    public static int TradeRequestDuration = 20;
    public static int UserPortalInvincibleTick = 5000;
    public static string UserPortalInvincibleIconPath = "./data/resource/image/skill/icon/deathInvincible.png";
    public static int SummonRideeDuration = 1000;
    public static int WorldMapAdjustTileX = 0;
    public static int WorldMapAdjustTileY = 0;
    public static float TimeScalePCScale = 0.1f;
    public static float TimeScalePCDuration = 1.0f;
    public static int GoToHomeCastingTime = 0;
    public static int returnHomeSkill = 100000000;
    public static int returnHomeSkillMeret = 100000013;
    public static int TutorialIntroSkipTime = 5;
    public static string AvatarDefaultItemMale = "10200032,10300198";
    public static string AvatarDefaultItemFemale = "10200033,10300199";
    public static int ModelHouse = 62000027;
    public static int TalkCooldown = 1000;
    public static int AddressPopupDuration = 3000;
    public static int MaxFPS = 120;
    public static int UGCShopSellMinPrice = 150;
    public static int UGCShopSellMaxPrice = 3000;
    public static int UGCShopSaleDay = 90;
    public static int UGCShopAdFeeMeret = 30;
    public static int UGCShopAdHour = 72;
    public static int UGCShopSellingRestrictAmount = 200000;
    public static int MeretMarketHomeBannerShowTick = 6000;
    public static int BlackMarketSellMinPrice = 100;
    public static int BlackMarketSellMaxPrice = 500000000;
    public static int BlackMarketSellEndDay = 2;
    public static int ItemTransferBlackMarketGrade = 4;
    public static int UgcBannerCheckTime = 4;
    public static int FastChat_CheckTime = 2000;
    public static int FastChat_CheckCount = 5;
    public static int SameChat_CheckTime = 3000;
    public static int SameChat_CheckCount = 5;
    public static int SameChat_RestrictTime = 10000;
    public static int FastChat_RestrictTime = 30000;
    public static int RestrictChat_AddRestrictTime = 10000;
    public static int AccumWarning_AddRestrictTime = 60000;
    public static int RestrictWarning_ReleaseTime = 10000;
    public static int MaxChatLength = 100;
    public static int UsingNoPhysXModelUserCount = 10;
    public static int UsingNoPhysXModelActorCount = 10;
    public static int UsingNoPhysXModelJointCount = 10;
    public static int EmotionBoreAnimProbability = 100;
    public static float FallMoveSpeed = 1.0f;
    public static int GuildCreatePrice = 2000;
    public static int GuildCreateMinLevel = 0;
    public static int GuildNameLengthMin = 2;
    public static int GuildNameLengthMax = 25;
    public static int guildFundMax = 20000;
    public static float guildFundRate = 0.1f;
    public static int guildExpMaxCountForPlayTime = 2;
    public static int guildDonateMeso = 10000;
    public static string mirrorGuideMoviePath = "Common/Customize_Hat.usm";
    public static string hairGuideMoviePath = "Common/Customize_Hair.usm";
    public static string makeUpGuideMoviePath = "Common/Customize_MakeUp.usm";
    public static int FastShimmerRadius = 600;
    public static int FastShimmerHeight = 450;
    public static int SmartRecommendNotify_DurationTick = 15000;
    public static int BootyPopupDuration = 3000;
    public static bool EnableSoundMute = true;
    public static int BossKillSoundRange = 1500;
    public static string charCreateGuideMoviePath = "Common/Customize_Intro.usm";
    public static int monsterPeakTimeNotifyDuration = 300;
    public static int KeyIsDownSkill_MaxDurationTick = 30000;
    public static int shadowWorldBuffHpUp = 70000027;
    public static int shadowWorldBuffMoveProtect = 70000032;
    public static int AirTaxiItemID = 20300003;
    public static int PeriodOfMaidEmployment = 30;
    public static int MaidReadyToPay = 7;
    public static int MaidAffinityMax = 10;
    public static int MeretRevivalDebuffCode = 100000001;
    public static float MeretRevivalFeeReduceLimit = 0.5f;
    public static int MeretConsumeWorldChat = 30;
    public static int MeretConsumeChannelChat = 3;
    public static int MeretConsumeSuperChat = 200;
    public static int pvpBtiRewardItem = 90000006;
    public static int pvpBtiRewardWinnerCount = 30;
    public static int pvpBtiRewardLoserCount = 10;
    public static int PvpFFAReward1Count = 30;
    public static int PvpFFAReward2Count = 25;
    public static int PvpFFAReward3Count = 20;
    public static int PvpFFAReward4Count = 15;
    public static int PvpFFAReward5Count = 15;
    public static int PvpFFAReward6Count = 15;
    public static int PvpFFAReward7Count = 15;
    public static int PvpFFAReward8Count = 10;
    public static int PvpFFAReward9Count = 10;
    public static int PvpFFAReward10Count = 10;
    public static int PvpFFARewardItem = 90000006;
    public static int PvpFFAAdditionRewardRate = 0;
    public static int MailExpiryDays = 30;
    public static int WorldMapBossTooltipCount = 30;
    public static int ShowNameTagEnchantItemGrade = 4;
    public static int ShowNameTagEnchantLevel = 12;
    public static int BossNotifyAbsLevel = 1;
    public static int RoomExitWaitSecond = 10;
    public static int AdditionalMesoMaxRate = 7;
    public static int AdditionalExpMaxRate = 9;
    public static int HonorTokenMax = 30000;
    public static int KarmaTokenMax = 75000;
    public static int LuTokenMax = 2000;
    public static int HaviTokenMax = 35000;
    public static int ReverseCoinMax = 2000;
    public static int MentorTokenMax = 10000; // From KMS
    public static int MenteeTokenMax = 35000; // From KMS
    public static int CharacterDestroyDivisionLevel = 20;
    public static int CharacterDestroyWaitSecond = 86400;
    public static int BossShimmerScaleUpActiveDistance = 5000;
    public static float BossShimmerScaleUpSize = 3.0f;
    public static int ShowNameTagSellerTitle = 10000153;
    public static int ShowNameTagChampionTitle = 10000152;
    public static int ShowNameTagTrophy1000Title = 10000170;
    public static int ShowNameTagTrophy2000Title = 10000171;
    public static int ShowNameTagTrophy3000Title = 10000172;
    public static int ShowNameTagArchitectTitle = 10000158;
    public static float SwimDashSpeed = 5.4f;
    public static int UserTriggerStateMax = 10;
    public static int UserTriggerEnterActionMax = 3;
    public static int UserTriggerConditionMax = 3;
    public static int UserTriggerConditionActionMax = 3;
    public static int PCBangAdditionalEffectID = 100000006;
    public static int PCBangAdditionalEffectExp = 1;
    public static int PCBangAdditionalEffectMeso = 2;
    public static int PCBangItemDefaultPeriod = 1440;
    public static int ShadowWorldAutoReviveDeadAction = 1;
    public static int GoodInteriorRecommendUICloseTime = 15;
    public static string UGCInfoDetailViewPage = "http://www.nexon.net/en/legal/user-generated-content-policy";
    public static int UGCInfoStoryBookID = 39000038;
    public static int HomePasswordUsersKickDelay = 10;
    public static string TriggerEditorHelpURL = "http://maplestory2.nexon.net/en/news/article/32326";
    public static int QuestRewardSAIgnoreLevel = 10;
    public static int RecallCastingTime = 3000;
    public static int PartyRecallMeret = 30;
    public static float CashCallMedicLeaveDelay = 0.5f;
    public static int characterMaxLevel = 99; // Updated
    public static int DropSPEPBallMaxLength = 300;
    public static int DropSPEPBallTargetZPos = 100;
    public static int DropSPEPBallPickUpVel = 250;
    public static int DropSPEPBallPickUpGravity = -120;
    public static float DropSPEPBallPickUpCompleteRotateTime = 0.05f;
    public static int DropSPEPBallPickUpCompleteRotateVel = 5;
    public static int EnchantItemBindingRequireLevel = 1;
    public static int enchantSuccessBroadcastingLevel = 12;
    public static int EnchantEquipIngredientMaxCount = 1000;
    public static int EnchantFailStackUsingMaxCount = 100;
    public static int EnchantFailStackTakeMaxCount = 1000;
    public static int EnchantEquipIngredientOpenLevel = 11;
    public static int EnchantEquipIngredientOpenRank = 4;
    public static int EnchantEquipIngredientMaxSuccessProb = 3000;
    public static int EnchantFailStackOpenLevel = 1;
    public static int EnchantFailStackTakeMaxSuccessProb = 10000;
    public static int BankCallDuration = 500;
    public static string NoticeDialogUrl = "http://nxcache.nexon.net/maplestory2/ingame-banners/index.html";
    public static string NoticeDialogUrlPubTest = "maview:/Game/BannerTest";
    public static int NoticeDialogOpenSeconds = 5000;
    public static int RemakeOptionMaxCount = 10;
    public static int FisherBoreDuration = 10000;
    public static string fishingStartCastingBarText0 = "s_fishing_start_castingbar_text0";
    public static string fishingStartCastingBarText1 = "s_fishing_start_castingbar_text1";
    public static string fishingStartCastingBarText2 = "s_fishing_start_castingbar_text2";
    public static string fishingStartCastingBarText3 = "s_fishing_start_castingbar_text3";
    public static string fishingStartCastingBarText4 = "s_fishing_start_castingbar_text4";
    public static string fishingStartBalloonText0 = "s_fishing_start_balloon_text0";
    public static string fishingStartBalloonText1 = "s_fishing_start_balloon_text1";
    public static string fishingStartBalloonText2 = "s_fishing_start_balloon_text2";
    public static string fishingStartBalloonText3 = "s_fishing_start_balloon_text3";
    public static string fishingStartBalloonText4 = "s_fishing_start_balloon_text4";
    public static string fishingStartBalloonText5 = "s_fishing_start_balloon_text5";
    public static string fishingStartBalloonText6 = "s_fishing_start_balloon_text6";
    public static string fishingStartBalloonText7 = "s_fishing_start_balloon_text7";
    public static string fishingStartBalloonText8 = "s_fishing_start_balloon_text8";
    public static string fishingStartBalloonText9 = "s_fishing_start_balloon_text9";
    public static string fishFightingCastingBarText0 = "s_fishing_fishfighting_castingbar_text0";
    public static string fishFightingBalloonText0 = "s_fishing_fishfighting_balloon_text0";
    public static string fishFightingBalloonText1 = "s_fishing_fishfighting_balloon_text1";
    public static string fishFightingBalloonText2 = "s_fishing_fishfighting_balloon_text2";
    public static string fishFightingBalloonText3 = "s_fishing_fishfighting_balloon_text3";
    public static string fishFightingBalloonText4 = "s_fishing_fishfighting_balloon_text4";
    public static string fishFightingBalloonText5 = "s_fishing_fishfighting_balloon_text5";
    public static int WorldMapSpecialFunctionNpcID0 = 11001276;
    public static string WorldMapSpecialFunctionNpcFrame0 = "airship_enabled";
    public static string WorldMapSpecialFunctionNpcTooltip0 = "s_worldmap_special_function_npc0";
    public static int WorldMapSpecialFunctionNpcID1 = 11001403;
    public static string WorldMapSpecialFunctionNpcFrame1 = "airship_enabled";
    public static string WorldMapSpecialFunctionNpcTooltip1 = "s_worldmap_special_function_npc0";
    public static int WarpOpenContinent0 = 102;
    public static int WarpOpenContinent1 = 103;
    public static int WarpOpenContinent2 = 202;
    public static int WarpOpenContinent3 = 105;
    public static string WriteMusicDetailWebPage = "http://maplestory2.nexon.net/en/news/article/32329";
    public static int WriteMusicStoryBookID = 39000047;
    public static int MusicListenInRadius = 900;
    public static int MusicListenOutRadius = 2200;
    public static int DungeonRoomMaxRewardCount = 99;
    public static int DungeonMatchRecommendPickCount = 6;
    public static int DungeonSeasonRankMinLevel = 99;
    public static int LimitMeretRevival = 1;
    public static int MinimapScaleSkipDuration = 5000;
    public static int MinimapScaleSkipSplitPixel = 20;
    public static int TradeMinMeso = 100;
    public static int TradeMaxMeso = 500000000;
    public static int TradeFeePercent = 20;
    public static int DailyMissionRequireLevel = 50;
    public static int MesoMarketBasePrice = 5000000;
    public static int MesoMarketProductUnit0 = 5000000;
    public static int MesoMarketBuyPayType = 16;
    public static int MesoMarketIconType = 0;
    public static string MesoMarketTokenDetailUrl = "http://maplestory2.nexon.net/en/news/article/45213";
    public static int BeautyHairShopGotoFieldID = 52000008;
    public static int BeautyHairShopGotoPortalID = 1;
    public static int BeautyColorShopGotoFieldID = 52000009;
    public static int BeautyColorShopGotoPortalID = 1;
    public static int BeautyFaceShopGotoFieldID = 52000010;
    public static int BeautyFaceShopGotoPortalID = 1;
    public static int BeautyStyleExpandSlotPrice = 980;
    public static int BeautyStyleMaxSlotCount = 0;
    public static int BeautyStyleDefaultSlotCount = 30;
    public static int BeautyStyleExpandSlotCount1time = 3;
    public static string CashShopFigureAddressPage = "http://maplestory2.nexon.com/cashshop/address";
    public static int NxaCashChargeWebPageWidth = 650;
    public static int NxaCashChargeWebPageHeight = 650;
    public static int ItemUnLockTime = 259200;
    public static int PropertyProtectionTime = 60;
    public static string TencentSecurityWebPage = "http://mxd2.qq.com/safe/index.shtml";
    public static int HomeBankCallDuration = 1000;
    public static int HomeBankCallCooldown = 30000;
    public static string HomeBankCallSequence = "Object_React_A";
    public static int HomeDoctorCallDuration = 1000;
    public static int HomeDoctorCallCooldown = 30000;
    public static string HomeDoctorCallSequence = "EmergencyHelicopter_A";
    public static int HomeDoctorNpcID = 11001668;
    public static int HomeDoctorScriptID0 = 1;
    public static int HomeDoctorScriptID1 = 10;
    public static int EnchantMasterScriptID = 31;
    public static int RestExpAcquireRate = 10000;
    public static int RestExpMaxAcquireRate = 100000;
    public static int ApartmentPreviewRequireLevel = 50;
    public static int ApartmentPreviewRequireQuestID = 90000060;
    public static int KeyboardGuideShowLevel = 13;
    public static int extendAutoFishMaxCount = 8;
    public static int extendAutoPlayInstrumentMaxCount = 8;
    public static int ResetShadowBuffMeret = 100;
    public static int InventoryExpandPrice1Row = 390;
    public static int VIPServicePeriodLimitDay = 100000000;
    public static int VIPMarketCommissionSale = 20;
    public static int BreedDuration = 767;
    public static int HarvestDuration = 767;
    public static int RestartQuestStartField = 52000056;
    public static int RestartQuestStartFieldRuneblader = 63000006;
    public static int RestartQuestStartFieldStriker = 63000015;
    public static int RestartQuestStartFieldSoulBinder = 63000035;
    public static int QuestPortalKeepTime = 300;
    public static string QuestPortalKeepNif = "Eff_Com_Portal_E_Quest";
    public static int QuestPortalDimensionY = 50;
    public static int QuestPortalDimensionZ = 350;
    public static int QuestPortalSummonTime = 600;
    public static int QuestPortalDistanceFromNpc = 200;
    public static int PetChangeNameMeret = 100;
    public static int PetRunSpeed = 350;
    public static int PetPickDistance = 1050;
    public static int PetSummonCastTime = 800;
    public static int PetBoreTime = 60000;
    public static int PetIdleTime = 70000;
    public static int PetTiredTime = 10000;
    public static int PetSkillTime = 13000;
    public static string PetEffectUse = "Pet/Eff_Pet_Use.xml";
    public static string PetEffectSkill = "Pet/Eff_Pet_Skill.xml";
    public static string PetEffectHappy = "Pet/Eff_Pet_Happy.xml";
    public static string PetGemChatBalloon = "pet";
    public static int PetTrapAreaDistanceEasy = 150;
    public static int PetTrapAreaDistanceNormal = 150;
    public static int PetTrapAreaDistanceHard = 150;
    public static string PetTrapAreaEffectEasy = "Pet/Eff_Pet_TrapInstallArea_easy.xml";
    public static string PetTrapAreaEffectNormal = "Pet/Eff_Pet_TrapInstallArea_normal.xml";
    public static string PetTrapAreaEffectHard = "Pet/Eff_Pet_TrapInstallArea_hard.xml";
    public static string PetTrapAreaEffectOtherUser = "Pet/Eff_Pet_TrapArea_OtherUser.xml";
    public static string PetTamingMaxPointEffect = "Pet/Eff_PetTaming_MaxPoint.xml";
    public static string PetTamingAttackMissEffect = "Pet/Eff_PetTaming_Attack_Miss.xml";
    public static string PetTrapDropItemEffect = "Pet/Eff_PetTrap_DropItem.xml";
    public static int TamingPetEscapeTime = 300;
    public static int TamingPetMaxPoint = 10000;
    public static int PetNameLengthMin = 2;
    public static int PetNameLengthMax = 25;
    public static int PetTrapDropVisibleDelay = 2000;
    public static int PetMaxLevel = 50;
    public static string VisitorBookURL = "";
    public static int OneShotSkillID = 19900061;
    public static int BagSlotTabGameCount = 48;
    public static int BagSlotTabSkinCount = 150;
    public static int BagSlotTabSummonCount = 48;
    public static int BagSlotTabMaterialCount = 48;
    public static int BagSlotTabMasteryCount = 126;
    public static int BagSlotTabLifeCount = 48;
    public static int BagSlotTabQuestCount = 48;
    public static int BagSlotTabGemCount = 48;
    public static int BagSlotTabPetCount = 60;
    public static int BagSlotTabPetEquipCount = 48;
    public static int BagSlotTabActiveSkillCount = 84;
    public static int BagSlotTabCoinCount = 48;
    public static int BagSlotTabBadgeCount = 60;
    public static int BagSlotTabMiscCount = 84;
    public static int BagSlotTabLapenshardCount = 48;
    public static int BagSlotTabPieceCount = 48;
    public static int BagSlotTabGameCountMax = 48;
    public static int BagSlotTabSkinCountMax = 150;
    public static int BagSlotTabSummonCountMax = 48;
    public static int BagSlotTabMaterialCountMax = 48;
    public static int BagSlotTabMasteryCountMax = 48;
    public static int BagSlotTabLifeCountMax = 48;
    public static int BagSlotTabQuestCountMax = 48;
    public static int BagSlotTabGemCountMax = 48;
    public static int BagSlotTabPetCountMax = 78;
    public static int BagSlotTabPetEquipCountMax = 48;
    public static int BagSlotTabActiveSkillCountMax = 48;
    public static int BagSlotTabCoinCountMax = 48;
    public static int BagSlotTabBadgeCountMax = 48;
    public static int BagSlotTabMiscCountMax = 48;
    public static int BagSlotTabLapenshardCountMax = 48;
    public static int BagSlotTabPieceCountMax = 48;
    public static int MasteryObjectInteractionDistance = 150;
    public static float GatheringObjectMarkOffsetX = 0.0f;
    public static float GatheringObjectMarkOffsetY = 0.0f;
    public static float BreedingObjectMarkOffsetX = 0.0f;
    public static float BreedingObjectMarkOffsetY = 0.0f;
    public static int UGCAttention = 0;
    public static int UGCInfringementCenter = 1;
    public static string CharacterSelectBoreIdleEffect_Ranger = "";
    public static string CharacterSelectBoreIdleEffect_SoulBinder = "";
    public static int DisableSoloPlayHighLevelDungeon = 0;
    public static int MergeSmithScriptID = 10;
    public static int AutoPressActionKeyDuration = 500;
    public static int WebBrowserSizeWidthMin = 438;
    public static int WebBrowserSizeWidthMax = 1700;
    public static int WebBrowserSizeHeightMin = 708;
    public static int WebBrowserSizeHeightMax = 1003;
    public static bool WebBrowserEnableSizingButton = true;
    public static int MeretAirTaxiPrice = 15;
    public static int GlobalPortalMinLevel = 10;
    public static int userMassiveExtraRewardMax = 5;
    public static int SkillBookTreeAddTabFeeMeret = 990;
    public static int MentorRequireLevel = 50;
    public static int MenteeRequireLevel = 30;
    public static int MentorMaxWaitingCount = 100;
    public static int MenteeMaxReceivedCount = 20;
    public static int FindDungeonHelpEasyDungeonLevel = 50;
    public static int CoupleEffectCheckTick = 5000;
    public static int CoupleEffectCheckRadius = 150;
    public static int FameContentsSkyFortressMapID0 = 02000421;
    public static int FameContentsSkyFortressMapID1 = 02000422;
    public static int FameContentsSkyFortressMapID2 = 52010039;
    public static int FameContentsSkyFortressMapID3 = 52010040;
    public static int AllianceQuestPickCount = 2;
    public static int FieldQuestPickCount = 1;
    public static int FameContentsSkyFortressGotoMapID = 02000422;
    public static int FameContentsSkyFortressGotoPortalID = 3;
    public static int FameContentsRequireQuestID = 91000013;
    public static int FameExpedContentsRequireQuestID = 50101050;
    public static int DailyPetEnchantMaxCount = 24;
    public static int MouseCursorHideTime = 30;
    public static int EnchantTransformScriptID = 10;
    public static float AutoHideGroupAlpha = 0.6f;
    public static int AutoHideGroupHitVisibleTick = 3000;
    public static int UgcShopCharRotateStartDegreeY = 178;
    public static int UgcShopCharRotateEndDegreeY = 8;
    public static int SurvivalScanAdditionalID = 71000052;
    public static int MapleSurvivalTopNRanking = 5;
    public static string MapleSurvivalSeasonRewardUrl =
        "http://maplestory2.nexon.net/en/news/article/32249/mushking-royale-championship-rewards";
    public static int TreeWateringEmotion = 10000;
    public static int AdventureLevelLimit = 10000;
    public static int AdventureLevelLvUpExp = 1000000;
    public static int AdventureLevelMaxExp = 1500000;
    public static float AdventureLevelFactor = 0.02f;
    public static int AdventureExpFactorElite = 10;
    public static int AdventureExpFactorBoss = 100;
    public static int AdventureLevelStartLevel = 50;
    public static int AdventureLevelLvUpRewardItem = 30001133;
    public static int NameColorDeadDuration = 2000;
    public static float MesoRevivalFeeReduceLimit = 0.5f;
    public static float IngredientFeeReduceLimit = 0.5f;
    public static int StatPointLimit_str = 100;
    public static int StatPointLimit_dex = 100;
    public static int StatPointLimit_int = 100;
    public static int StatPointLimit_luk = 100;
    public static int StatPointLimit_hp = 100;
    public static int StatPointLimit_cap = 60;
    public static float GamePadRumbleMultiple = 3.0f;
    public static int NurturingEatMaxCount = 0;
    public static int NurturingPlayMaxCount = 3;
    public static string NurturingQuestTag = "NurturingGhostCats";
    public static int NurturingDuration = 3000;
    public static int NurturingInteractionDistance = 150;
    public static int NurturingEatGrowth = 10;
    public static int NurturingPlayGrowth = 10;
    public static int NurturingPlayMailId = 19101804;
    public static int NurturingPlayMaxGrowth = 3;
    public static int NurturingHungryTime = 1000;
    public static int SkillPointLimitLevel1 = 80;
    public static int SkillPointLimitLevel2 = 70;
    public static int SellPriceNormalMax = 4628;
    public static int SellPriceRareMax = 5785;
    public static int SellPriceEliteMax = 7405;
    public static int SellPriceExcellentMax = 9256;
    public static int SellPriceLegendaryMax = 11339;
    public static int SellPriceArtifactMax = 13653;
    public static string RegionServerUrl_de = "http://ugc.maplestory2.nexon.net/region/region_DE.xml";
    public static string RegionServerUrl_en = "http://ugc.maplestory2.nexon.net/region/region_EN.xml";
    public static string RegionServerUrl_bpo = "http://ugc.maplestory2.nexon.net/region/region_BPO.xml";
    public static int HoldAttackSkillID = 10700252;
    public static int TooltipLabelMaxWidth = 408;
    public static int ClubNameLengthMin = 2;
    public static int ClubNameLengthMax = 25;
    public static int ClubMaxCount = 3;
    public static int UgcNameLengthMin = 3;
    public static int UgcNameLengthMax = 25;
    public static int UgcTagLengthMax = 12;
    public static int ChangeJobLevel = 60;
    public static int LapenshardOpenQuestID = 20002391;
    public static int MaidNameLengthMin = 1;
    public static int MaidNameLengthMax = 35;
    public static int MaidDescLengthMin = 1;
    public static int MaidDescLengthMax = 35;
    public static int GamePadStickMoveValue = 50;
    public static int HighlightMenuUsingLevel = 5;
    public static int PartyVoteReadyDurationSeconds = 20;
    public static int PartyVoteReadyTagExpireSeconds = 10;
    public static int ShieldBarOffsetY = -10;
    public static int MouseInteractLimitDistance = 2000;
    public static int AutoInstallEquipmentMinLevel = 5;
    public static int AutoInstallEquipmentMaxLevel = 49;
    public static string PartySearchRegisterComboValues = "4,6,10";
    public static int StatScaleMarkingAdditionalEffect = 70000174;
    public static string DungeonRewardFailEmotions = "90200001,90200009,90200005,90200018";
    public static int SummonPetSkillID = 82100001;
    public static int UGCMapSetItemEffectCountLimit = 10;
    public static string DiscordAppID = "555204064091045904";
    public static int ItemBoxMultiOpenMaxCount = 10;
    public static int ItemBoxMultiOpenLimitCount = 500;
    public static int BuffBalloonDistance = 3800;
    public static int PaybackStartDate = 20191024;
    public static int PaybackMailId = 50000020;
    public static int PaybackMailPeriodDay = 90;
    public static int PaybackMaxRewardMeret = 10000;
    public static string PaybackGuideUrl = "http://maplestory2.nexon.com/News/Events";
    public static int DummyNpcMale = 2040998;
    public static int DummyNpcFemale = 2040999;
    public static int DummyNpc(Gender gender) => gender is Gender.Female ? DummyNpcFemale : DummyNpcMale;
    #endregion

    #region server table/constants.xml
    public static int NextStateTriggerDefaultTick = 100;
    public static int UserRevivalPaneltyTick = 3600000;
    public static int UserRevivalPaneltyMinLevel = 10;
    public static int maxDeadCount = 3;
    public static byte hitPerDeadCount = 5;
    public static int FishFightingProp = 3000;

    public static float NpcLastSightRadius = 1800;
    public static float NpcLastSightHeightUp = 525;
    public static float NpcLastSightHeightDown = 225;

    public static int RecoveryHPWaitTick = 1000;
    public static int RecoverySPWaitTick = 1000;
    public static int RecoveryEPWaitTick = 1000;
    public static float FallBoundingAddedDistance = 750f;

    public static int UserBattleDurationTick = 5000;

    public static int SystemShopNPCIDConstruct = 11000486;
    public static int SystemShopNpcIDUGCDesign = 11000166;
    public static int SystemShopNPCIDHonorToken = 11001562;
    public static int SystemShopNPCIDFishing = 11001609;
    public static int SystemShopNPCIDMentor = 11003561;
    public static int SystemShopNPCIDMentee = 11003562;

    public static TimeSpan GlobalCubeSkillIntervalTime = TimeSpan.FromMilliseconds(100);
    #endregion
}

#pragma warning restore IDE1006 // Naming Styles
