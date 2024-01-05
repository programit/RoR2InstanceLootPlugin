using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using R2API;
using R2API.Networking.Interfaces;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using static HG.Reflection.SearchableAttribute;

[assembly: OptIn]


namespace InstanceLootPlugin
{
    /// <summary>
    /// This plugin is used to provide instance based loot when you're running with "Artifact of Sacrifice" and "Artifact of Command" enabled.
    /// The way it works is when a monster dies it would normally drop a CommandCube gameobject which is a network object.
    /// On interaction it spawns an item.
    ///
    /// This mod overrides that behavior. Instead of spawning a network CommandCube on drop. We hook OnDropletHitGroundServer.
    /// The hook sends a network message to all clients and tells each client to spawn a commandcube gameobject locally.
    ///
    /// Since object is local no one but the local client can see it and interact. Since it isn't a real network object we need
    /// to also hook some of the interaction actions around it as otherwise you'll fail to interact since the default logic expects
    /// a network object which the server doesn't know about.
    ///
    /// Once an item is selected a game object for that item type is spawned, this one is spawned on the server by sending a message \
    /// to the server to run the logic to spawn. If we just tried to spawn locally we could get errors on clients as they are not allowed to spawn
    /// network objects, thus the message to the server. Since the item is spawned on the server it can be picked up now by anyone but assuming that's ok
    /// since the item spawns in front of the character interacting anyway.
    ///
    /// 2 other features provided by the plugin
    /// 1) Press F3 will pull all CommandCube objects to you in a circle, so any objects you might have missed aren't lost
    /// 2) A console command `drop_rate <value>` which you can use to set the drop rate of items from monsters
    /// </summary>
    [BepInDependency(ItemAPI.PluginGUID)]
    [BepInDependency("com.bepis.r2api")]
    [BepInDependency(LanguageAPI.PluginGUID)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class InstanceLootPlugin : BaseUnityPlugin
    {
        // The Plugin GUID should be a unique ID for this plugin,
        // which is human readable (as it is used in places like the config).
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "programit";
        public const string PluginName = "InstanceBasedLoot";
        public const string PluginVersion = "2.0.0";
        private static ConfigEntry<float> DropChanceMultiplier { get; set; }
        private static ConfigEntry<float> MinimumDropChance { get; set; }
        private static ConfigEntry<float> BaseDropChance { get; set; }
        private static ConfigEntry<bool> EnablePlayerDropRateScaling { get; set; }
        private static ConfigEntry<bool> EnableSwarmsScaling { get; set; }
        private static ConfigEntry<bool> EnableBadLuckProtection { get; set; }

        // Matches current default drop rate. Differs from BaseDropChance because we override it for player scaling.
        public static float dropChance = 5;

        // Internal state indicating whether or not the mod hooks have been registered or not.
        private static bool IsModHooked = false;

        // We will occasionally remove the hooks and not allow them to be readded.
        // We do this because there is an artifact key that spawns when you load into bulwark's ambry.
        // This is significant because this mod is incompatible with non-commandcube pickups.
        // Leaving the mod active causes an NRE in "artifact trial" code, which results in artifacts not being
        // awarded for successful clears.
        // To work around this, we simply unhook the mod for artifact world.
        private bool forceDisabled = false;

        // Persist drop rates that have been manually set via the console. Resets between runs.
        private static bool useManualDropRate = false;

        private static DropRateManager dropRateManager;

        public void Awake()
        {
            Log.Init(Logger);

            // Read configs from file
            DropChanceMultiplier = base.Config.Bind<float>(
                "General",
                "DropChanceMultiplier",
                1f,
                "Manipulate drop rate. Use `0.5` to halve or `2` to double."
            );

            MinimumDropChance = base.Config.Bind<float>(
                "General", "MinimumDropChance",
                1f,
                "The lowest possible drop chance when player count scaling is active."
            );

            EnablePlayerDropRateScaling = base.Config.Bind<bool>(
                "General",
                "EnablePlayerBasedDropRateScaling",
                true,
                "Enabling this will reduce drop rates to account for shared loot."
            );

            EnableSwarmsScaling = base.Config.Bind<bool>(
                "General",
                "EnableSwarmsScaling",
                true,
                "Enabling this will (correctly) reduce drop rates for Artifact of Swarms."
            );

            EnableBadLuckProtection = base.Config.Bind<bool>(
                "General",
                "EnableBadLuckProtection",
                true,
                "Enabling this will increase drop rate consistency with low drop rates."
            );

            BaseDropChance = base.Config.Bind<float>(
                "General",
                "BaseDropChance",
                5f,
                "Base item drop chance. It is recommended to leave this at the default value."
            );
            dropChance = BaseDropChance.Value;

            dropRateManager = new DropRateManager(
                EnableBadLuckProtection,
                EnableSwarmsScaling,
                DropChanceMultiplier,
                BaseDropChance,
                MinimumDropChance);
        }

        private void Update()
        {
            // Pulls all items on map to you.
            if (Input.GetKeyDown(KeyCode.F3) && IsModHooked)
            {
                try
                {
                    GenericPickupController.FindObjectsOfType<RuleBookViewer>();
                    var items = GenericPickupController.FindObjectsOfType<PickupDisplay>();
                    var playerPos = ((MPEventSystem)EventSystem.current).localUser.cachedMaster.GetBodyObject()
                        .transform.position;
                    float radius = 10f;
                    for (int i = 0; i < items.Length; i++)
                    {
                        var obj = items[i];

                        if (!obj.highlight.name.StartsWith("CommandCube"))
                        {
                            continue;
                        }

                        float angle = i * Mathf.PI * 2f / items.Length;
                        Vector3 newPos = new Vector3(playerPos.x + Mathf.Cos(angle) * radius, playerPos.y,
                            playerPos.z + Mathf.Sin(angle) * radius);

                        obj.gameObject.transform.position = newPos;
                    }
                }
                catch (NullReferenceException e)
                {
                    Logger.LogDebug("Failed to pull CommandCubes with F3 - most likely because there were none.");
                }
            }
        }

        // This is largely a workaround to provide a workaround for drops not being interactable when a player joins
        // a lobby with "DropInMultiplier" after the activeSceneChanged event has fired.
        // This cause of this issue is that the hooks have not been loaded, so this serves as a simple way for users to
        // trigger a hook load.
        // If they don't trigger this event, starting the next stage will fix it for them automatically.
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                ReevaluateLifecycle();
            }
        }

        private void OnEnable()
        {

            // This event is called:
            // - after the boss dies in teleporter events
            // - the end of each stage of the Voidling and Mithrix fights
            // - after the Gilded Coast and A Moment Whole encounters
            //
            // It considers player count, Shrine of the Mountain activations, boss item drop chance, and other factors
            // and then spawns the necessary items
            On.RoR2.BossGroup.DropRewards += (orig, self) =>
            {
                if (EnablePlayerDropRateScaling.Value && IsModHooked)
                {
                    self.scaleRewardsByPlayerCount = false;
                }

                orig.Invoke(self);
            };

            // Handles the general state of the mod
            SceneManager.activeSceneChanged += (oldScene, newScene) =>
            {
                bool isMenuScene = newScene.name == "loadingbasic"
                                   || newScene.name == "intro"
                                   || newScene.name == "splash"
                                   || newScene.name == "title"
                                   || newScene.name == "eclipseworld" // Eclipse menu
                                   || newScene.name == "infinitetowerworld" // Simulacrum menu
                                   || newScene.name == "lobby";

                // Maintains disabled state so that tabbing into the game can't re-hook in Bulwark's Ambry.
                forceDisabled = newScene.name == "artifactworld" || isMenuScene;

                // Reset manual drop rates and drop rate tracking between runs
                if (isMenuScene)
                {
                    useManualDropRate = false;
                    DropRateTracker.ResetTracker();
                }

                // Update drop rate and (un)hook if necessary
                ReevaluateLifecycle();
            };

            // Register message which we use to spawn the item itself
            R2API.Networking.NetworkingAPI.RegisterMessageType<SpawnCustomMessage2>();
        }


        private void ReevaluateLifecycle()
        {
            // We recalculate drop rate each stage to account for any new or missing players
            if (EnablePlayerDropRateScaling.Value && IsSacrificeEnabled() && !useManualDropRate)
                dropChance = dropRateManager.GetPlayerAwareBaseDropChance(GetPlayerCount(), dropChance);

            if (forceDisabled)
            {
                if (IsModHooked)
                {
                    Log.Warning("Force Disabled: Will remove hooks, even if Artifact of Command is enabled.");
                    UnHook();
                }
                else
                {
                    Log.Debug("Force Disabled: Will not hook.");
                }
                return;
            }

            bool commandEnabled = IsCommandEnabled();
            if (IsModHooked && !commandEnabled)
                UnHook();
            else if (!IsModHooked && commandEnabled)
                Hook();
        }

        private void Hook()
        {
            Logger.LogInfo("Loading hooks");

            // Sets drop chance
            On.RoR2.Util.GetExpAdjustedDropChancePercent += Util_GetExpAdjustedDropChancePercent;

            //// Hooks network handler for setup
            On.RoR2.Networking.NetworkMessageHandlerAttribute.RegisterClientMessages += NetworkMessageHandlerAttribute_RegisterClientMessages;

            // Hooks pickup creation to control drop on client
            On.RoR2.Artifacts.CommandArtifactManager.OnDropletHitGroundServer += CommandArtifactManager_OnDropletHitGroundServer;

            // Hook interaction since we didn't generate a real network object
            On.RoR2.Interactor.AttemptInteraction += Interactor_AttemptInteraction;

            // Override the choice again since this isn't a real network object we'd have issue.
            On.RoR2.PickupPickerController.SubmitChoice += PickupPickerController_SubmitChoice;

            // Ignore since we'll create ourselves
            On.RoR2.GenericPickupController.CreatePickup += GenericPickupController_CreatePickup;

            IsModHooked = true;
        }

        private void UnHook()
        {
            Logger.LogInfo("Unloading hooks");

            On.RoR2.Util.GetExpAdjustedDropChancePercent -= Util_GetExpAdjustedDropChancePercent;
            On.RoR2.Networking.NetworkMessageHandlerAttribute.RegisterClientMessages -= NetworkMessageHandlerAttribute_RegisterClientMessages;
            On.RoR2.Artifacts.CommandArtifactManager.OnDropletHitGroundServer -= CommandArtifactManager_OnDropletHitGroundServer;
            On.RoR2.Interactor.AttemptInteraction -= Interactor_AttemptInteraction;
            On.RoR2.PickupPickerController.SubmitChoice -= PickupPickerController_SubmitChoice;
            On.RoR2.GenericPickupController.CreatePickup -= GenericPickupController_CreatePickup;

            IsModHooked = false;
        }

        private GenericPickupController GenericPickupController_CreatePickup(On.RoR2.GenericPickupController.orig_CreatePickup orig, ref GenericPickupController.CreatePickupInfo createPickupInfo)
        {
            return null;
        }

        private void PickupPickerController_SubmitChoice(On.RoR2.PickupPickerController.orig_SubmitChoice orig, PickupPickerController self, int choiceIndex)
        {
            if ((ulong)choiceIndex >= (ulong)((long)self.options.Length))
            {
                return;
            }

            ref PickupPickerController.Option ptr = ref self.options[choiceIndex];
            if (!ptr.available)
            {
                return;
            }

            PickupPickerController.PickupIndexUnityEvent pickupIndexUnityEvent = self.onPickupSelected;
            if (pickupIndexUnityEvent == null)
            {
                return;
            }

            pickupIndexUnityEvent.Invoke(ptr.pickupIndex.value); // This spawns an instance of the item when client==server
            Log.Info($"Sending CreatePickup {self.options[choiceIndex].pickupIndex}");

            new SpawnCustomMessage2(self.options[choiceIndex].pickupIndex, ((MPEventSystem)EventSystem.current).localUser.cachedMaster.GetBodyObject().transform.position)
                .Send(R2API.Networking.NetworkDestination.Server);

            self.OnDisplayEnd(null, null, null);
        }

        private void Interactor_AttemptInteraction(On.RoR2.Interactor.orig_AttemptInteraction orig, Interactor self, GameObject interactableObject)
        {
            if (interactableObject.name.StartsWith("CommandCube"))
            {
                var picker = interactableObject.GetComponent<PickupPickerController>();
                picker.panelInstance = UnityEngine.Object.Instantiate<GameObject>(picker.panelPrefab, ((MPEventSystem)EventSystem.current).localUser.cameraRigController.hud.mainContainer.transform);
                picker.panelInstanceController = picker.panelInstance.GetComponent<PickupPickerPanel>();
                picker.panelInstanceController.pickerController = picker;
                picker.panelInstanceController.SetPickupOptions(picker.options);
            }
            else
            {
                orig(self, interactableObject);
            }
        }

        private void CommandArtifactManager_OnDropletHitGroundServer(On.RoR2.Artifacts.CommandArtifactManager.orig_OnDropletHitGroundServer orig, ref GenericPickupController.CreatePickupInfo createPickupInfo, ref bool shouldSpawn)
        {
            DropRateTracker.RegisterItemDrop(createPickupInfo);

            NetworkServer.SendToAll(969, new SpawnCustomMessage() { position = createPickupInfo.position, pickupIndex = createPickupInfo.pickupIndex });
            shouldSpawn = false;
        }

        /// <summary>
        ///  Overrides the default registration so we can add our own message handler along with default
        /// </summary>
        private void NetworkMessageHandlerAttribute_RegisterClientMessages(On.RoR2.Networking.NetworkMessageHandlerAttribute.orig_RegisterClientMessages orig, NetworkClient client)
        {
            orig(client);
            client.RegisterHandler(969, SpawnCustomController.HandlePickupMessage);
        }

        /// <summary>
        /// Sets drop rate
        /// </summary>
        private static float Util_GetExpAdjustedDropChancePercent(
            On.RoR2.Util.orig_GetExpAdjustedDropChancePercent orig,
            float nativeBaseDropChance, UnityEngine.GameObject characterBodyObject)
        {
            // Drop chance depends on a number of factors.
            // These factors include enemy elite level, teleporter event activity, and Artifact of Swarms enablement.
            // There are also entities with a drop chance of 0%, such as the "HealingAffix" spawned by slain mending elites
            // For more details on drop rates read https://riskofrain2.fandom.com/wiki/Artifacts#Sacrifice
            float nativeDropChance = orig.Invoke(nativeBaseDropChance, characterBodyObject);

            // Perform custom drop chance logic
            float finalDropChance = dropRateManager.ComputeDropChance(IsSwarmEnabled(), nativeDropChance, nativeBaseDropChance, dropChance);
            Log.Debug($"DropChance: original_base={nativeBaseDropChance} original_final={nativeDropChance}; plugin_base={dropChance}; plugin_final={finalDropChance} (Character={characterBodyObject.name})");

            DropRateTracker.RegisterDropOpportunity(finalDropChance);

            // Perform Bad Luck Protection, if necessary
            if (dropRateManager.NeedsBadLuckProtection(finalDropChance, Run.instance.GetRunStopwatch()))
            {
                return dropRateManager.GetBadLuckProtectedDropChance(finalDropChance);
            }

            return finalDropChance;
        }

        [ConCommand(commandName = "drop_rate", flags = ConVarFlags.ExecuteOnServer, helpText = "Sets Drop rate for items")]
        private static void CCSetDropRate(ConCommandArgs args)
        {
            dropChance = args.GetArgFloat(0);
            useManualDropRate = true;
        }

        [ConCommand(commandName = "drop_rate_report", flags = ConVarFlags.ExecuteOnServer, helpText = "View Drop Rate Report")]
        private static void CCGetDropRateReport(ConCommandArgs args)
        {
            if (IsModHooked)
            {
                DropRateTracker.LogReport();
            }
        }

        private static bool IsSacrificeEnabled()
        {
            return RunArtifactManager.instance is not null
                   && RunArtifactManager.instance.isActiveAndEnabled
                   && RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.sacrificeArtifactDef);
        }

        private static bool IsCommandEnabled()
        {
            return RunArtifactManager.instance is not null
                   && RunArtifactManager.instance.isActiveAndEnabled
                   && RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.commandArtifactDef);
        }

        private static bool IsSwarmEnabled()
        {
            return RunArtifactManager.instance is not null
                   && RunArtifactManager.instance.isActiveAndEnabled
                   && RunArtifactManager.instance.IsArtifactEnabled(RoR2Content.Artifacts.swarmsArtifactDef);
        }

        private int GetPlayerCount()
        {
            return PlayerCharacterMasterController.instances.Count(pc => pc.isConnected);
        }
    }
}
