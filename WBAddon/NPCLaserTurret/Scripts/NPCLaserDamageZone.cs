
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SaccFlightAndVehicles
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class NPCLaserDamageZone : UdonSharpBehaviour
    {
        [Tooltip("This turret's SaccEntity (to prevent self-damage)")]
        public SaccEntity OwnerEntity;
        [Tooltip("Damage dealt per second while laser is in contact")]
        public float DamagePerSecond = 50f;
        [Tooltip("Weapon type ID for kill feed")]
        public byte WeaponType = 1;
        [Tooltip("Layers to detect as valid targets")]
        [SerializeField] private int[] TargetLayers = { 17, 31 };

        private void OnTriggerStay(Collider other)
        {
            if (!other) return;

            // Check if the hit object is on a valid target layer
            int hitLayer = other.gameObject.layer;
            bool validLayer = false;
            for (int i = 0; i < TargetLayers.Length; i++)
            {
                if (hitLayer == TargetLayers[i])
                {
                    validLayer = true;
                    break;
                }
            }
            if (!validLayer) return;

            // Walk up the hierarchy to find a SaccEntity
            SaccEntity victimEntity = null;
            Transform current = other.transform;
            while (current != null)
            {
                SaccEntity entity = current.GetComponent<SaccEntity>();
                if (entity != null)
                {
                    victimEntity = entity;
                    break;
                }
                current = current.parent;
            }

            if (victimEntity == null) return;
            if (victimEntity == OwnerEntity) return;
            if (victimEntity._dead || victimEntity.invincible) return;

            // Victim Authority: only process damage if local player owns the victim
            if (!victimEntity.IsOwner) return;

            // Apply damage through SaccEntity's damage pipeline
            float damage = DamagePerSecond * Time.deltaTime;
            victimEntity.LastHitDamage = damage;
            victimEntity.LastHitWeaponType = WeaponType;
            if (OwnerEntity)
            {
                victimEntity.LastAttacker = OwnerEntity;
            }
            victimEntity.SendEventToExtensions("SFEXT_G_BulletHit");
        }
    }
}
