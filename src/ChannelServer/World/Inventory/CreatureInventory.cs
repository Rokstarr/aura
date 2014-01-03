﻿// Copyright (c) Aura development team - Licensed under GNU GPL
// For more information, see licence.txt in the main folder

using System;
using System.Collections.Generic;
using System.Linq;
using Aura.Shared.Util;
using Aura.Shared.Mabi.Const;
using Aura.Channel.World.Entities;
using Aura.Data.Database;
using Aura.Channel.Network.Sending;

namespace Aura.Channel.World
{
	public class CreatureInventory
	{
		private const int DefaultWidth = 6;
		private const int DefaultHeight = 10;
		private const int GoldItemId = 2000;
		private const int GoldStackMax = 1000;

		private Creature _creature;
		private Dictionary<Pocket, InventoryPocket> _pockets;

		/// <summary>
		/// List of all items in this inventory.
		/// </summary>
		public IEnumerable<Item> Items
		{
			get
			{
				foreach (var pocket in _pockets.Values)
					foreach (var item in pocket.Items.Where(a => a != null))
						yield return item;
			}
		}

		/// <summary>
		/// List of all items sitting in equipment pockets in this inventory.
		/// </summary>
		public IEnumerable<Item> Equipment
		{
			get
			{
				foreach (var pocket in _pockets.Values.Where(a => a.Pocket.IsEquip()))
					foreach (var item in pocket.Items.Where(a => a != null))
						yield return item;
			}
		}

		/// <summary>
		/// List of all items in equipment slots, minus hair and face.
		/// </summary>
		public IEnumerable<Item> ActualEquipment
		{
			get
			{
				foreach (var pocket in _pockets.Values.Where(a => a.Pocket.IsEquip() && a.Pocket != Pocket.Hair && a.Pocket != Pocket.Face))
					foreach (var item in pocket.Items.Where(a => a != null))
						yield return item;
			}
		}

		private WeaponSet _weaponSet;
		/// <summary>
		/// Sets or returns the selected weapon set.
		/// </summary>
		public WeaponSet WeaponSet
		{
			get { return _weaponSet; }
			set
			{
				_weaponSet = value;
				this.UpdateEquipReferences(Pocket.RightHand1, Pocket.LeftHand1, Pocket.Magazine1);
			}
		}

		public Item RightHand { get; protected set; }
		public Item LeftHand { get; protected set; }
		public Item Magazine { get; protected set; }

		public int Gold
		{
			get { return this.Count(GoldItemId); }
		}

		public CreatureInventory(Creature creature)
		{
			_creature = creature;

			_pockets = new Dictionary<Pocket, InventoryPocket>();

			// Cursor, Temp
			this.Add(new InventoryPocketStack(Pocket.Temporary));
			this.Add(new InventoryPocketSingle(Pocket.Cursor));

			// Equipment
			for (var i = Pocket.Face; i <= Pocket.Accessory2; ++i)
				this.Add(new InventoryPocketSingle(i));

			// Style
			for (var i = Pocket.ArmorStyle; i <= Pocket.RobeStyle; ++i)
				this.Add(new InventoryPocketSingle(i));
		}

		/// <summary>
		/// Adds pocket to inventory.
		/// </summary>
		/// <param name="inventoryPocket"></param>
		public void Add(InventoryPocket inventoryPocket)
		{
			if (_pockets.ContainsKey(inventoryPocket.Pocket))
				Log.Warning("Replacing pocket '{0}' in '{1}'s inventory.", inventoryPocket.Pocket, _creature);

			_pockets[inventoryPocket.Pocket] = inventoryPocket;
		}

		/// <summary>
		/// Adds main inventories (inv, personal, VIP). Call after creature's
		/// defaults (RaceInfo) have been loaded.
		/// </summary>
		public void AddMainInventory()
		{
			if (_creature.RaceData == null)
				Log.Warning("Race for creature '{0}' ({1}) not loaded before initializing main inventory.", _creature.Name, _creature.EntityIdHex);

			var width = (_creature.RaceData != null ? _creature.RaceData.InventoryWidth : DefaultWidth);
			var height = (_creature.RaceData != null ? _creature.RaceData.InventoryHeight : DefaultHeight);

			this.Add(new InventoryPocketNormal(Pocket.Inventory, width, height));
			this.Add(new InventoryPocketNormal(Pocket.PersonalInventory, width, height));
			this.Add(new InventoryPocketNormal(Pocket.VIPInventory, width, height));
		}

		/// <summary>
		/// Returns true if pocket exists in this inventory.
		/// </summary>
		/// <param name="pocket"></param>
		/// <returns></returns>
		public bool Has(Pocket pocket)
		{
			return _pockets.ContainsKey(pocket);
		}

		/// <summary>
		/// Returns item with the id, or null.
		/// </summary>
		/// <param name="itemId"></param>
		/// <returns></returns>
		public Item GetItem(long itemId)
		{
			foreach (var pocket in _pockets.Values)
			{
				var item = pocket.GetItem(itemId);
				if (item != null)
					return item;
			}

			return null;
		}

		/// <summary>
		/// Returns item at the location, or null.
		/// </summary>
		/// <param name="pocket"></param>
		/// <param name="x"></param>
		/// <param name="y"></param>
		/// <returns></returns>
		public Item GetItemAt(Pocket pocket, int x, int y)
		{
			if (!this.Has(pocket))
				return null;

			return _pockets[pocket].GetItemAt(x, y);
		}

		/// <summary>
		/// Adds item at target location. Returns true if successful.
		/// </summary>
		public bool Move(Item item, Pocket target, byte targetX, byte targetY)
		{
			if (!this.Has(target))
				return false;

			var source = item.Info.Pocket;
			var amount = item.Info.Amount;

			Item collidingItem = null;
			if (!_pockets[target].TryAdd(item, targetX, targetY, out collidingItem))
				return false;

			// If amount differs (item was added to stack)
			if (collidingItem != null && item.Info.Amount != amount)
			{
				Send.ItemAmount(_creature, collidingItem);

				// Left overs, update
				if (item.Info.Amount > 0)
				{
					Send.ItemAmount(_creature, item);
				}
				// All in, remove from cursor.
				else
				{
					_pockets[item.Info.Pocket].Remove(item);
					Send.ItemRemove(_creature, item);
				}
			}
			else
			{
				// Remove the item from the source pocket
				_pockets[source].Remove(item);

				// Toss it in, it should be the cursor.
				if (collidingItem != null)
					_pockets[source].Add(collidingItem);

				Send.ItemMoveInfo(_creature, item, source, collidingItem);
			}

			this.UpdateInventory(item, source, target);

			return true;
		}

		/// <summary>
		/// Tries to add item to pocket. Returns false if the pocket
		/// doesn't exist or there was no space.
		/// </summary>
		public bool Add(Item item, Pocket pocket)
		{
			if (!_pockets.ContainsKey(pocket))
				return false;

			var success = _pockets[pocket].Add(item);
			if (success)
			{
				Send.ItemNew(_creature, item);
				this.UpdateEquipReferences(pocket);
			}

			return success;
		}

		/// <summary>
		/// Adds item to pocket at the position it currently has.
		/// Returns false if pocket doesn't exist.
		/// </summary>
		public bool InitAdd(Item item, Pocket pocket)
		{
			if (!_pockets.ContainsKey(pocket))
				return false;

			_pockets[pocket].AddUnsafe(item);
			return true;
		}

		/// <summary>
		/// Tries to add item to one of the main inventories, using the temp
		/// inv as fallback (if specified to do so). Returns false if
		/// there was no space.
		/// </summary>
		public bool Add(Item item, bool tempFallback)
		{
			bool success;

			// Try inv
			success = _pockets[Pocket.Inventory].Add(item);

			// Try temp
			if (!success && tempFallback)
				success = _pockets[Pocket.Temporary].Add(item);

			// Inform about new item
			if (success)
				Send.ItemNew(_creature, item);

			return success;
		}

		public bool Insert(Item item, bool tempFallback)
		{
			if (item.Data.StackType == StackType.Stackable)
			{
				// Try stacks/sacs first
				List<Item> changed;
				_pockets[Pocket.Inventory].FillStacks(item, out changed);
				this.UpdateChangedItems(changed);

				// Add new item stacks as long as needed.
				while (item.Info.Amount > item.Data.StackMax)
				{
					var newStackItem = new Item(item);
					newStackItem.Info.Amount = item.Data.StackMax;

					// Break if no new items can be added (no space left)
					if (!_pockets[Pocket.Inventory].Add(newStackItem))
						break;

					Send.ItemNew(_creature, newStackItem);
					item.Info.Amount -= item.Data.StackMax;
				}

				if (item.Info.Amount == 0)
					return true;
			}

			return this.Add(item, tempFallback);
		}

		public bool PickUp(Item item)
		{
			// Try stacks/sacs first
			if (item.Data.StackType == StackType.Stackable)
			{
				List<Item> changed;
				_pockets[Pocket.Inventory].FillStacks(item, out changed);
				this.UpdateChangedItems(changed);
			}

			// Add new items as long as needed
			while (item.Info.Amount > 0)
			{
				// Sadly generates a new id every time, but it's kinda hard to
				// change the items' position for the pocket, while we still
				// need its region position for broadcasting disappearance.
				var newStackItem = new Item(item);
				newStackItem.Info.Amount = Math.Min(item.Info.Amount, item.Data.StackMax);

				// Stop if no new items can be added (no space left)
				if (!_pockets[Pocket.Inventory].Add(newStackItem))
					break;

				Send.ItemNew(_creature, newStackItem);
				item.Info.Amount -= newStackItem.Info.Amount;
			}

			// Remove from map if item is in inv 100%
			if (item.Info.Amount == 0)
			{
				_creature.Region.RemoveItem(item);
				return true;
			}

			return false;

			//if (item.Data.StackType == StackType.Stackable)
			//{
			//    // Try stacks/sacs first
			//    List<Item> changed;
			//    _pockets[Pocket.Inventory].FillStacks(item, out changed);
			//    this.UpdateChangedItems(changed);

			//    // Add new item stacks as long as needed.
			//    while (item.Info.Amount > item.Data.StackMax)
			//    {
			//        var newStackItem = new Item(item);
			//        newStackItem.Info.Amount = item.Data.StackMax;

			//        // Break if no new items can be added (no space left)
			//        if (!_pockets[Pocket.Inventory].Add(newStackItem))
			//            break;

			//        Send.ItemNew(_creature, newStackItem);
			//        item.Info.Amount -= item.Data.StackMax;
			//    }

			//    // Success if item was completely filled into the inv
			//    if (item.Info.Amount == 0)
			//    {
			//        _creature.Region.RemoveItem(item);
			//        return true;
			//    }
			//    // Fail if there's more than the max left (inv is full)
			//    else if (item.Info.Amount > item.Data.StackMax)
			//        return false;
			//}

			//var success = _pockets[Pocket.Inventory].Add(item);
			//if (success)
			//{
			//    _creature.Region.RemoveItem(item);
			//    Send.ItemNew(_creature, item);
			//}

			//return success;
		}

		public void Debug()
		{
			(_pockets[Pocket.Inventory] as InventoryPocketNormal).TestMap();

			Send.ServerMessage(_creature, this.WeaponSet.ToString());
			if (this.RightHand == null)
				Send.ServerMessage(_creature, "null");
			else
				Send.ServerMessage(_creature, this.RightHand.ToString());
			if (this.LeftHand == null)
				Send.ServerMessage(_creature, "null");
			else
				Send.ServerMessage(_creature, this.LeftHand.ToString());
			if (this.Magazine == null)
				Send.ServerMessage(_creature, "null");
			else
				Send.ServerMessage(_creature, this.Magazine.ToString());
		}

		public bool Remove(Item item)
		{
			foreach (var pocket in _pockets.Values)
			{
				if (pocket.Remove(item))
				{
					this.UpdateInventory(item, item.Info.Pocket, Pocket.None);

					Send.ItemRemove(_creature, item);
					return true;
				}
			}

			return false;
		}

		private void UpdateInventory(Item item, Pocket source, Pocket target)
		{
			this.CheckLeftHand(item, source, target);
			this.UpdateEquipReferences(source, target);
			this.CheckEquipMoved(item, source, target);
		}

		private void UpdateChangedItems(IEnumerable<Item> items)
		{
			if (items == null)
				return;

			foreach (var item in items)
			{
				if (item.Info.Amount > 0 || item.Data.StackType == StackType.Sac)
					Send.ItemAmount(_creature, item);
				else
					Send.ItemRemove(_creature, item);
			}
		}

		/// <summary>
		/// Unequips item in left hand/magazine, if item in right hand is moved.
		/// </summary>
		/// <param name="item"></param>
		/// <param name="source"></param>
		/// <param name="target"></param>
		private void CheckLeftHand(Item item, Pocket source, Pocket target)
		{
			var pocketOfInterest = Pocket.None;

			if (source == Pocket.RightHand1 || source == Pocket.RightHand2)
				pocketOfInterest = source;
			if (target == Pocket.RightHand1 || target == Pocket.RightHand2)
				pocketOfInterest = target;

			if (pocketOfInterest != Pocket.None)
			{
				var leftPocket = pocketOfInterest + 2; // Left Hand 1/2
				var leftItem = _pockets[leftPocket].GetItemAt(0, 0);
				if (leftItem == null)
				{
					leftPocket += 2; // Magazine 1/2
					leftItem = _pockets[leftPocket].GetItemAt(0, 0);
				}
				if (leftItem != null)
				{
					// Try inventory first.
					// TODO: List of pockets stuff can be auto-moved to.
					var success = _pockets[Pocket.Inventory].Add(leftItem);

					// Fallback, temp inv
					if (!success)
						success = _pockets[Pocket.Temporary].Add(leftItem);

					if (success)
					{
						_pockets[leftPocket].Remove(leftItem);

						Send.ItemMoveInfo(_creature, leftItem, leftPocket, null);
						Send.EquipmentMoved(_creature, leftPocket);
					}
				}
			}
		}

		private void UpdateEquipReferences(params Pocket[] toCheck)
		{
			var firstSet = (this.WeaponSet == WeaponSet.First);
			var updatedHands = false;

			foreach (var pocket in toCheck)
			{
				// Update all "hands" at once, easier.
				if (!updatedHands && pocket >= Pocket.RightHand1 && pocket <= Pocket.Magazine2)
				{
					this.RightHand = _pockets[firstSet ? Pocket.RightHand1 : Pocket.RightHand2].GetItemAt(0, 0);
					this.LeftHand = _pockets[firstSet ? Pocket.LeftHand1 : Pocket.LeftHand2].GetItemAt(0, 0);
					this.Magazine = _pockets[firstSet ? Pocket.Magazine1 : Pocket.Magazine2].GetItemAt(0, 0);

					// Don't do it twice.
					updatedHands = true;
				}
			}
		}

		private void CheckEquipMoved(Item item, Pocket source, Pocket target)
		{
			if (source.IsEquip())
				Send.EquipmentMoved(_creature, source);

			if (target.IsEquip())
				Send.EquipmentChanged(_creature, item);
		}

		public bool Decrement(Item item, ushort amount = 1)
		{
			if (!this.Has(item) || item.Info.Amount == 0 || item.Info.Amount < amount)
				return false;

			item.Info.Amount -= amount;

			if (item.Info.Amount > 0 || item.Data.StackType == StackType.Sac)
			{
				Send.ItemAmount(_creature, item);
			}
			else
			{
				this.Remove(item);
				Send.ItemRemove(_creature, item);
			}

			return true;
		}

		public bool Has(Item item)
		{
			foreach (var pocket in _pockets.Values)
				if (pocket.Has(item))
					return true;

			return false;
		}

		public bool Add(int itemId, int amount = 1)
		{
			var newItem = new Item(itemId);

			if (newItem.Data.StackType == StackType.Stackable)
			{
				newItem.Info.Amount = (ushort)Math.Min(amount, ushort.MaxValue);
				return this.Insert(newItem, true);
			}
			else if (newItem.Data.StackType == StackType.Sac)
			{
				newItem.Info.Amount = (ushort)Math.Min(amount, ushort.MaxValue);
				return this.Add(newItem, true);
			}
			else
			{
				for (int i = 0; i < amount; ++i)
					this.Add(new Item(itemId), true);
				return true;
			}
		}

		public bool AddGold(int amount)
		{
			// Add gold, stack for stack
			do
			{
				var stackAmount = Math.Min(GoldStackMax, amount);
				this.Add(GoldItemId, stackAmount);
				amount -= stackAmount;
			}
			while (amount > 0);

			return true;
		}

		public bool Remove(int itemId, int amount = 1)
		{
			if (amount < 0)
				amount = 0;

			var changed = new List<Item>();


			foreach (var pocket in _pockets.Values)
			{
				amount -= pocket.Remove(itemId, amount, ref changed);

				if (amount == 0)
					break;
			}

			this.UpdateChangedItems(changed);

			return (amount == 0);
		}

		public bool RemoveGold(int amount)
		{
			return this.Remove(GoldItemId, amount);
		}

		public int Count(int itemId)
		{
			var result = 0;

			foreach (var pocket in _pockets.Values)
				result += pocket.Count(itemId);

			return result;
		}

		public bool Has(int itemId, int amount = 1)
		{
			return (this.Count(itemId) >= amount);
		}

		public bool HasGold(int amount)
		{
			return this.Has(GoldItemId, amount);
		}
	}

	public enum WeaponSet : byte
	{
		First = 0,
		Second = 1,
	}
}