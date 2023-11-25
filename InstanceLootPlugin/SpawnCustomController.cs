using RoR2;
using RoR2.Artifacts;
using RoR2.Networking;
using UnityEngine;
using UnityEngine.Networking;

namespace InstanceLootPlugin
{
    
    public class SpawnCustomController : NetworkBehaviour
    {
        /// <summary>
        /// Per Client handle that Receives messages when items are dropped and creates an item locally.
        /// </summary>
        [NetworkMessageHandler(msgType = 969, client = true)]
        public static void HandlePickupMessage(NetworkMessage netMsg)
        {
            SpawnCustomMessage pickupMessage = new SpawnCustomMessage();
            netMsg.ReadMessage<SpawnCustomMessage>(pickupMessage);

            Log.Info($"HandlePickupMessage {pickupMessage.pickupIndex.value}");

            CreatePickupDroplet(pickupMessage.pickupIndex, pickupMessage.position);

        }

        public static void CreatePickupDroplet(PickupIndex pickupIndex, Vector3 position)
        {
            var pickupInfo = new GenericPickupController.CreatePickupInfo
            {
                rotation = Quaternion.identity,
                pickupIndex = pickupIndex
            };
      
            GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(CommandArtifactManager.commandCubePrefab, position, pickupInfo.rotation);
            var pickupIndexNetworker = gameObject.GetComponent<PickupIndexNetworker>();
            var picker = gameObject.GetComponent<PickupPickerController>();

            picker.SetOptionsInternal(PickupPickerController.GetOptionsFromPickupIndex(pickupInfo.pickupIndex));

            if (!NetworkServer.active)
            {
                picker.SetDirtyBit(PickupPickerController.optionsDirtyBit);
            }

            pickupIndexNetworker.NetworkpickupIndex = pickupInfo.pickupIndex;
            if (pickupIndexNetworker.pickupDisplay)
            {
                pickupIndexNetworker.pickupDisplay.SetPickupIndex(pickupInfo.pickupIndex, false);
            }
        }

        /// <summary>
        ///  Creates item, impl is from GenericPickupController.CreatePickup
        /// </summary>
        /// <param name="pickupIndex"></param>
        /// <param name="position"></param>
        public static void CreatePickupItem(PickupIndex pickupIndex, Vector3 position)
        {
            if (!NetworkServer.active)
            {
                return;
            }

            var pickupInfo = new GenericPickupController.CreatePickupInfo
            {
                rotation = Quaternion.identity,
                position = position,
                pickupIndex = pickupIndex,
            };

            GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(pickupInfo.prefabOverride ?? GenericPickupController.pickupPrefab, position, pickupInfo.rotation);
            GenericPickupController component = gameObject.GetComponent<GenericPickupController>();
            if (component)
            {
                GenericPickupController genericPickupController = component;
                GenericPickupController.CreatePickupInfo createPickupInfo2 = pickupInfo;
                genericPickupController.NetworkpickupIndex = createPickupInfo2.pickupIndex;
            }
            PickupIndexNetworker component2 = gameObject.GetComponent<PickupIndexNetworker>();
            if (component2)
            {
                PickupIndexNetworker pickupIndexNetworker = component2;
                GenericPickupController.CreatePickupInfo createPickupInfo2 = pickupInfo;
                pickupIndexNetworker.NetworkpickupIndex = createPickupInfo2.pickupIndex;
            }
            PickupPickerController component3 = gameObject.GetComponent<PickupPickerController>();
            if (component3 && pickupInfo.pickerOptions != null)
            {
                component3.SetOptionsServer(pickupInfo.pickerOptions);
            }

            NetworkServer.Spawn(gameObject);
        }
    }
}
