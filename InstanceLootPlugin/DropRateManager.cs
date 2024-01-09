using System;
using BepInEx.Configuration;

namespace InstanceLootPlugin;

public class DropRateManager
{
    private ConfigEntry<bool> EnableBadLuckProtection;
    private ConfigEntry<bool> EnableSwarmsScaling;
    private ConfigEntry<float> DropChanceMultiplier;
    private ConfigEntry<float> BaseDropChance;
    private ConfigEntry<float> MinimumDropChance;

    public DropRateManager(
        ConfigEntry<bool> enableBadLuckProtection,
        ConfigEntry<bool> enableSwarmsScaling,
        ConfigEntry<float> dropChanceMultiplier,
        ConfigEntry<float> baseDropChance,
        ConfigEntry<float> minimumDropChance)
    {
        EnableBadLuckProtection = enableBadLuckProtection;
        EnableSwarmsScaling = enableSwarmsScaling;
        DropChanceMultiplier = dropChanceMultiplier;
        BaseDropChance = baseDropChance;
        MinimumDropChance = minimumDropChance;
    }

    public float GetPlayerAwareBaseDropChance(int playerCount, float dropChance)
    {
        float playerBasedDropRateMultiplier = 1f / playerCount;
        float multiplier = playerBasedDropRateMultiplier * DropChanceMultiplier.Value;
        float newDropChance = Math.Max(BaseDropChance.Value * multiplier, MinimumDropChance.Value);

        if (!dropChance.Equals(newDropChance))
        {
            dropChance = newDropChance;
            Log.Debug(
                $"Using dropRate of {dropChance}% (base={BaseDropChance.Value}, multiplier={DropChanceMultiplier.Value}, playerCount={playerCount}, minimumDropChance={MinimumDropChance.Value}");
        }

        return dropChance;
    }

    public float ComputeDropChance(bool isSwarmEnabled, float nativeDropChance, float nativeBaseDropChance, float dropChance)
    {
        // Approximately balance Swarms Artifact since they didn't do a good job of it.
        float swarmModifiedNativeChance = !(EnableSwarmsScaling.Value && isSwarmEnabled) || nativeDropChance == 0
            ? nativeDropChance
            : (nativeDropChance - nativeBaseDropChance) * 0.6f + nativeBaseDropChance;

        // Apply the multipliers used by the game to respect the many drop rate factors.
        float baseModifier = nativeBaseDropChance / 5f;
        float enemyModifier = swarmModifiedNativeChance / nativeBaseDropChance;

        return dropChance * baseModifier * enemyModifier;
    }

    public bool NeedsBadLuckProtection(float actualDropChance, float time)
    {
        if (EnableBadLuckProtection.Value && DropRateTracker.cumulativeDropChance >= 100 && actualDropChance > 0)
        {
            var behindOnDrops = DropRateTracker.expectedItems > DropRateTracker.totalItemDrops;
            var itemsPerMinute = DropRateTracker.totalItemDrops / (time / 60);
            return behindOnDrops || itemsPerMinute < 0.8;
        }

        return false;
    }

    public float GetBadLuckProtectedDropChance(float dropChance)
    {
        float badLuckBoost = DropRateTracker.cumulativeDropChance - 100;
        float totalDropChance = Math.Min(dropChance + badLuckBoost, 90);
        Log.Debug($"Bad Luck Protection triggered! ({dropChance}% => {totalDropChance}%)");
        return totalDropChance;
    }
}
