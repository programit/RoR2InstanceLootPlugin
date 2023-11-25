using BepInEx;
using R2API;
using R2API.Networking.Interfaces;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
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
    [BepInDependency(LanguageAPI.PluginGUID)]
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class InstanceLootPlugin : BaseUnityPlugin
    {
        // The Plugin GUID should be a unique ID for this plugin,
        // which is human readable (as it is used in places like the config).
        // If we see this PluginGUID as it is on thunderstore,
        // we will deprecate this mod.
        // Change the PluginAuthor and the PluginName !
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "programit";
        public const string PluginName = "InstanceBasedLoot";
        public const string PluginVersion = "1.0.0";

        public void Awake()
        {
            Log.Init(Logger);
        }

        // Matches current default drop rate
        public static float dropChance = 5;

        private void Update()
        {
            // Pulls all items on map to you. 
            if (Input.GetKeyDown(KeyCode.F3))
            {
                GenericPickupController.FindObjectsOfType<RuleBookViewer>();
                var items = GenericPickupController.FindObjectsOfType<PickupDisplay>();
                var playerPos = ((MPEventSystem)EventSystem.current).localUser.cachedMaster.GetBodyObject().transform.position;
                float radius = 10f;
                for (int i = 0; i < items.Length; i++)
                {
                    var obj = items[i];

                    if (!obj.highlight.name.StartsWith("CommandCube"))
                    {
                        continue;
                    }

                    float angle = i * Mathf.PI * 2f / items.Length;
                    Vector3 newPos = new Vector3(playerPos.x + Mathf.Cos(angle) * radius, playerPos.y, playerPos.z + Mathf.Sin(angle) * radius);

                    obj.gameObject.transform.position = newPos;
                }
            }
        }

        private void OnEnable()
        {            
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

            // Register message which we use to spawn the item itself
            R2API.Networking.NetworkingAPI.RegisterMessageType<SpawnCustomMessage2>();

            // Ignore since we'll create ourselves
            On.RoR2.GenericPickupController.CreatePickup += GenericPickupController_CreatePickup;
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
            pickupIndexUnityEvent.Invoke(ptr.pickupIndex.value);    // This spawns an isntance of the item when client==server
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
        private static float Util_GetExpAdjustedDropChancePercent(On.RoR2.Util.orig_GetExpAdjustedDropChancePercent orig, float baseChancePercent, UnityEngine.GameObject characterBodyObject)
        {
            return dropChance;
        }

        [ConCommand(commandName = "drop_rate", flags = ConVarFlags.ExecuteOnServer, helpText = "Sets Drop rate for items")]
        private static void CCSetDropRate(ConCommandArgs args)
        {
            dropChance = args.GetArgFloat(0);
        }
    }
}
