# Efficiency Analysis Report - SaccFlightAndVehicles

This report identifies several areas in the codebase where efficiency could be improved.

## Issue 1: Redundant GetComponent Call in EntityRespawn

**File:** `Scripts/SaccEntity.cs` (Line 1171)
**Severity:** Low-Medium

The `EntityRespawn()` method calls `GetComponent<Rigidbody>()` every time it is invoked, despite the class already having a cached reference to the rigidbody in `VehicleRigidbody`.

```csharp
// Current code:
Rigidbody rb = GetComponent<Rigidbody>();

// Should use cached reference:
Rigidbody rb = VehicleRigidbody;
```

**Impact:** `GetComponent` is a relatively expensive operation that involves searching through components. While this method isn't called frequently, using the cached reference is a simple optimization.

## Issue 2: String Operations in Hot Path (SendEventToExtensions)

**File:** `Scripts/SaccEntity.cs` (Lines 1112, 1133-1136)
**Severity:** Medium

The `SendEventToExtensions` method performs string operations inside loops:

1. `eventname.Contains("_Passenger")` is called for each passenger function controller
2. Multiple chained `.Replace()` calls create intermediate string allocations

```csharp
// Line 1112:
if (eventname.Contains("_Passenger"))

// Lines 1133-1136:
passengerEventName = passengerEventName.Replace("G_PilotEnter", "G_PassengerEnter")
    .Replace("G_PilotExit", "G_PassengerExit")
    .Replace("O_PilotEnter", "P_PassengerEnter")
    .Replace("O_PilotExit", "P_PassengerExit");
```

**Impact:** String operations allocate memory and can cause GC pressure. This method is called frequently during gameplay events.

## Issue 3: Oversized Array Allocation in GetExtentions

**File:** `Scripts/SaccEntity.cs` (Lines 1210, 1234-1235)
**Severity:** Low

The `GetExtentions` method allocates an array sized for all possible extensions, then copies to a correctly-sized array at the end:

```csharp
var result = new UdonSharpBehaviour[entity.ExtensionUdonBehaviours.Length + entity.Dial_Functions_L.Length + entity.Dial_Functions_R.Length];
// ... populate ...
var finalResult = new UdonSharpBehaviour[count];
System.Array.Copy(result, finalResult, count);
```

**Impact:** Creates two array allocations when one could suffice with a different approach (e.g., using a List or two-pass counting).

## Issue 4: Repeated GetProgramVariable Calls in Update Loops

**File:** `Scripts/SaccGroundVehicle/SaccGroundVehicle.cs` (Lines 518-538)
**Severity:** Medium

The `LateUpdate` method repeatedly calls `GetProgramVariable("Grounded")` for each wheel in multiple loops:

```csharp
for (int i = 0; i < SteerWheels.Length; i++)
{
    if ((bool)SteerWheels[i].GetProgramVariable("Grounded"))
    {
        NumGroundedSteerWheels++;
    }
}
```

**Impact:** `GetProgramVariable` involves reflection-like operations. This runs every frame when the vehicle is active.

## Issue 5: New Vector3 Allocations in FixedUpdate

**File:** `Scripts/SaccAirVehicle/Weapons/SAV_AAMController.cs` (Line 240)
**Severity:** Low

Creating new Vector3 instances in FixedUpdate:

```csharp
AAMRigid.AddRelativeForce(new Vector3(-sidespeed * AirPhysicsStrength, -downspeed * AirPhysicsStrength, 0), ForceMode.Acceleration);
```

**Impact:** While Vector3 is a struct in Unity (no heap allocation), this pattern appears throughout the codebase and could be optimized by reusing cached vectors where appropriate.

## Recommendations

1. **Immediate Fix:** Replace `GetComponent<Rigidbody>()` with `VehicleRigidbody` in `EntityRespawn()` - simple, safe, and improves code consistency.

2. **Future Consideration:** Consider caching the result of string operations or using string comparison methods that don't allocate.

3. **Architecture:** For frequently accessed properties like wheel grounded state, consider exposing them as public fields rather than using `GetProgramVariable`.
