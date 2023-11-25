using R2API.Networking.Interfaces;
using RoR2;
using Unity;
using UnityEngine;
using UnityEngine.Networking;

namespace InstanceLootPlugin
{
    public class SpawnCustomMessage : MessageBase, INetMessage
    {
        public override void Serialize(NetworkWriter writer)
        {
            writer.Write(this.position);
            GeneratedNetworkCode._WritePickupIndex_None(writer, this.pickupIndex);
        }

        public override void Deserialize(NetworkReader reader)
        {
            this.position = reader.ReadVector3();
            this.pickupIndex = GeneratedNetworkCode._ReadPickupIndex_None(reader);
        }

        public void OnReceived() => SpawnCustomController.CreatePickupItem(pickupIndex, position);

        public Vector3 position;

        public PickupIndex pickupIndex;
    }

    public struct SpawnCustomMessage2 : R2API.Networking.Interfaces.INetMessage
    {
        public void Serialize(NetworkWriter writer)
        {
            writer.Write(this.position);
            GeneratedNetworkCode._WritePickupIndex_None(writer, this.pickupIndex);
        }

        public void Deserialize(NetworkReader reader)
        {
            this.position = reader.ReadVector3();
            this.pickupIndex = GeneratedNetworkCode._ReadPickupIndex_None(reader);
        }

        public void OnReceived() => SpawnCustomController.CreatePickupItem(pickupIndex, position);

        public SpawnCustomMessage2(PickupIndex pickupIndex, Vector3 position)
        {
            this.position = position;
            this.pickupIndex = pickupIndex;
        }

        public Vector3 position;

        public PickupIndex pickupIndex;
    }
}
