using CompanionServer.Handlers;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Color = UnityEngine.Color;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Property Buying", "Pwenill", "1.0.2")]
    [Description("Put in sale properties (house, building, etc) so that players can buy them, rented")]
    class PropertyBuying : RustPlugin
    {
        #region Variables
        [PluginReference]
        private Plugin Economics;

        Dictionary<ulong, PropertyHouse> _instanceEditingProperty = new Dictionary<ulong, PropertyHouse>();
        Dictionary<ulong, string> _instancePropertyManagerUI = new Dictionary<ulong, string>();
        Dictionary<ulong, string> _instancePropertyUI = new Dictionary<ulong, string>();
        Dictionary<int, PropertyHouse> _whitelistTemp = new Dictionary<int, PropertyHouse>();
        Dictionary<int, PropertyHouse> _whitelistPaymentTemp = new Dictionary<int, PropertyHouse>();

        private PropertyData propertyData;

        // Plugins Load
        public bool economicsPluginActive = false;

        // Permissions
        public string PropertyManagerPermissions = "propertybuying.manager.use";

        // UI
        public string PropertyBuyingParentUI = "PropertyBuying.UI";
        public string PropertyManagerParentUI = "PropertyManager.UI";
        #endregion

        #region Class
        private class PropertyData
        {
            public Dictionary<string, PropertyHouse> house = new Dictionary<string, PropertyHouse>();
            public Dictionary<string, PropertySubscriber> subscriber = new Dictionary<string, PropertySubscriber>();
        }
        private class PropertySubscriber
        {
            public string PayerID;

            public int Amount;
            public int SubscriberAttempt;
            public DateTime SubscriberDate;

            public PropertySubscriber(string payerid, DateTime subdate, int amount)
            {
                PayerID = payerid;
                SubscriberAttempt = 0;
                SubscriberDate = subdate;
                Amount = amount;
            }
        }
        private class PropertyHouse
        {
            public string Id;
            public string Name;
            public ulong Mailbox;
            public PropertyBuy Buying;
            public List<ulong> CodeLock = new List<ulong>();

            public PropertyHouse()
            {
                Mailbox = 0;
                Buying = new PropertyBuy();
            }
        }
        private class PropertyBuy
        {
            public int Price;
            public int Leased;
            public string Payer;
            public string Property;
            public int PurchaseAmount;
            public List<PropertyWhitelist> Whitelist = new List<PropertyWhitelist>();

            public PropertyBuy()
            {
                PurchaseAmount = 0;
                Price = -1;
                Leased = -1;
                Payer = "";
                Property = "";
                Whitelist = new List<PropertyWhitelist>();
            }
        }
        private class PropertyWhitelist
        {
            public string UserID;
            public string Name;
            public PropertyWhitelist()
            {

            }
            public PropertyWhitelist(string userid, string name)
            {
                UserID = userid;
                Name = name;
            }
        }
        #endregion

        #region Config
        private PluginConfig config;
        private class PluginConfig
        {
            public int DivisionSelling;
            public bool PurchaseEconomics;
            public int AttemptToTerminate;
            public float InviteExpire;
            public int PurchaseLimit;
            public string TimeForPayLeased;
            public string ColorNotification;
        }
        private void Init()
        {
            permission.RegisterPermission(PropertyManagerPermissions, this);

            config = Config.ReadObject<PluginConfig>();
        }
        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }
        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                DivisionSelling = 2,
                TimeForPayLeased = "2m",
                AttemptToTerminate = 5,
                InviteExpire = 20f,
                ColorNotification = "#601099",
                PurchaseEconomics = false,
                PurchaseLimit = -1,
            };
        }
        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region Hooks
        void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity.ShortPrefabName != "mailbox.deployed")
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(entity.OwnerID.ToString(), out property))
                return;

            ShowUI(player, property);
        }
        void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            PropertyDestroyUI(player);
        }
        void OnEntitySpawned(BaseNetworkable network)
        {
            BaseEntity entity = network as BaseEntity;
            if (entity == null)
                return;

            // Check whitelist entity for add
            if (entity.ShortPrefabName != "lock.code" && entity.ShortPrefabName != "mailbox.deployed")
                return;

            BasePlayer player = BasePlayer.FindByID(entity.OwnerID);
            if (!player)
                return;

            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(entity.OwnerID, out property))
                return;

            // Change the ownerid for custom id after find entity
            entity.OwnerID = (ulong)new System.Random().Next();

            if (entity.ShortPrefabName == "lock.code")
            {
                // Get parent code lock
                BaseEntity parentCodeLock = entity.GetParentEntity();
                if (entity.GetParentEntity() == null)
                    return;

                // Set parent code lock new owner id
                parentCodeLock.OwnerID = entity.OwnerID;

                property.CodeLock.Add(entity.OwnerID);
                SendReply(player, $"<color={config.ColorNotification}>[Property Buying]</color> {string.Format(lang.GetMessage("CodeLockAdding", this), property.Name)}");

                return;
            }

            if (entity.ShortPrefabName == "mailbox.deployed")
            {
                property.Mailbox = entity.OwnerID;
                SendReply(player, $"<color={config.ColorNotification}>[Property Buying]</color> {string.Format(lang.GetMessage("MailboxSet", this), property.Name)}");
                return;
            }
        }
        void Loaded()
        {
            if (Economics != null)
                economicsPluginActive = true;

            propertyData = Interface.Oxide.DataFileSystem.ReadObject<PropertyData>("PropertyBuying");

            foreach (var item in propertyData.subscriber)
                TimerPayment(item.Key, item.Value.SubscriberDate);
        }
        void OnServerSave()
        {
            Interface.Oxide.DataFileSystem.WriteObject("PropertyBuying", propertyData);
        }
        object CanUnlock(BasePlayer player, BaseLock baseLock)
        {
            var codeLock = propertyData.house.Select(x => x.Value).OfType<PropertyHouse>().Select(x => x.CodeLock.Contains(baseLock.OwnerID));
            if (codeLock != null)
                return false;

            return null;
        }
        object OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (entity.ShortPrefabName == "mailbox.deployed")
                return false;
            return null;
        }
        void OnPlayerInput(BasePlayer player, InputState input)
        {
            Item activeItem = player.GetActiveItem();

            if (activeItem == null)
                return;

            int instanceID = activeItem.info.GetInstanceID();

            PropertyHouse property;
            if (_whitelistTemp.TryGetValue(instanceID, out property))
            {
                if (input.WasJustReleased(BUTTON.FIRE_PRIMARY))
                {
                    var playerWhitelist = property.Buying.Whitelist.Where(x => x.UserID == player.UserIDString).FirstOrDefault();
                    if (playerWhitelist == null)
                    {
                        property.Buying.Whitelist.Add(new PropertyWhitelist(player.UserIDString, player.displayName));
                        player.SendConsoleCommand(string.Format("gametip.showtoast 0 \"{0}\"", lang.GetMessage("InviteAccept", this)));

                        CodeLockManager(property, "single_whitelist", player.OwnerID);

                        // Remove
                        _whitelistTemp.Remove(instanceID);
                        activeItem.DoRemove();
                    }
                }

                if (input.WasJustReleased(BUTTON.FIRE_SECONDARY))
                {
                    player.SendConsoleCommand(string.Format("gametip.showtoast 1 \"{0}\"", lang.GetMessage("InviteDecline", this)));

                    // Remove
                    _whitelistTemp.Remove(instanceID);
                    activeItem.DoRemove();
                }
            }
            if (_whitelistPaymentTemp.TryGetValue(instanceID, out property))
            {
                if (input.WasJustReleased(BUTTON.FIRE_PRIMARY))
                {
                    PropertySubscriber subscriber;
                    if (propertyData.subscriber.TryGetValue(property.Mailbox.ToString(), out subscriber))
                    {
                        subscriber.PayerID = player.UserIDString;

                        player.SendConsoleCommand(string.Format("gametip.showtoast 0 \"{0}\"", lang.GetMessage("InviteAccept", this)));

                        // Remove
                        _whitelistPaymentTemp.Remove(instanceID);
                        activeItem.DoRemove();
                    }
                }

                if (input.WasJustReleased(BUTTON.FIRE_SECONDARY))
                {
                    player.SendConsoleCommand(string.Format("gametip.showtoast 1 \"{0}\"", lang.GetMessage("InviteDecline", this)));

                    // Remove
                    _whitelistPaymentTemp.Remove(instanceID);
                    activeItem.DoRemove();
                }
            }
        }
        #endregion

        #region Console Command
        #region General
        [ConsoleCommand("property.page")]
        private void PropertySwitchUICommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs(2))
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[1], out property))
                return;

            _instancePropertyUI[player.userID] = arg.Args[0];
            ShowUI(player, property);
        }

        [ConsoleCommand("property.buy")]
        private void PropertyBuyCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (property.Buying.Property != "")
            {
                ShowUI(player, property, lang.GetMessage("PropertyAlreadyOwner", this));
                return;
            }

            int purchase_count = propertyData.house.Select(x => x.Value).Where(x => x.Buying.Payer == player.UserIDString).Count();
            if (purchase_count >= config.PurchaseLimit && config.PurchaseLimit != -1)
            {
                ShowUI(player, property, lang.GetMessage("PurchaseLimit", this));
                return;
            }

            if (!CheckPlayerBalancePay(player, property.Buying.Price, property.Name))
            {
                ShowUI(player, property, lang.GetMessage("NoFounds", this));
                return;
            }

            property.Buying.Property = player.UserIDString;
            property.Buying.Payer = player.UserIDString;
            property.Buying.PurchaseAmount = property.Buying.Price;
            property.Buying.Whitelist.Add(new PropertyWhitelist(player.UserIDString, player.displayName));

            ShowUI(player, property);
            CodeLockManager(property, "whitelist");
        }

        [ConsoleCommand("property.leased.pay")]
        private void PropertyLeasedPayCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            PropertySubscriber subscriber;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (!propertyData.subscriber.TryGetValue(arg.Args[0], out subscriber))
                return;

            if (subscriber.PayerID != player.UserIDString)
            {
                if (property.Buying.Property != player.UserIDString)
                {
                    ShowUI(player, property, lang.GetMessage("NoOwner", this));
                    return;
                }
            }

            int amount = subscriber.Amount * subscriber.SubscriberAttempt;
            if (!CheckPlayerBalancePay(player, amount, property.Name))
            {
                ShowUI(player, property, lang.GetMessage("NoFounds", this));
                return;
            }

            propertyData.subscriber[arg.Args[0]].SubscriberAttempt = 0;
            ShowUI(player, property);
        }

        [ConsoleCommand("property.lease")]
        private void PropertyLeasedCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (property.Buying.Property != "")
            {
                ShowUI(player, property, lang.GetMessage("PropertyAlreadyOwner", this));
                return;
            }

            int purchase_count = propertyData.house.Select(x => x.Value).Where(x => x.Buying.Payer == player.UserIDString).Count();
            if (purchase_count >= config.PurchaseLimit && config.PurchaseLimit != -1)
            {
                ShowUI(player, property, lang.GetMessage("PurchaseLimit", this));
                return;
            }

            if (!CheckPlayerBalancePay(player, property.Buying.Leased, property.Name))
            {
                ShowUI(player, property, lang.GetMessage("NoFounds", this));
                return;
            }

            property.Buying.PurchaseAmount = property.Buying.Leased;
            property.Buying.Payer = player.UserIDString;
            property.Buying.Property = player.UserIDString;

            property.Buying.Whitelist.Add(new PropertyWhitelist(player.UserIDString, player.displayName));

            DateTime now = DateTime.Now;
            propertyData.subscriber.Add(property.Mailbox.ToString(), new PropertySubscriber(property.Buying.Payer.ToString(), now, property.Buying.Leased));

            TimerPayment(property.Mailbox.ToString(), now);

            ShowUI(player, property);
            CodeLockManager(property, "whitelist");
        }

        [ConsoleCommand("property.sell")]
        private void PropertySellCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (property.Buying.Payer != player.UserIDString)
            {
                if (property.Buying.Property != player.UserIDString)
                {
                    ShowUI(player, property, lang.GetMessage("NoOwner", this));
                    return;
                }
            }

            RefundThePlayer(player, property);

            SellingProperty(property);
            ShowUI(player, property);
        }

        [ConsoleCommand("property.user.add")]
        private void PropertyWhitelistAddCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (property.Buying.Property != player.UserIDString)
                return;

            Item item = ItemManager.CreateByName("note");
            item.name = string.Format(lang.GetMessage("NoteTitleTenant", this), property.Name);
            item.text = string.Format(lang.GetMessage("NoteTextTenant", this), property.Name, player.displayName);
            player.GiveItem(item);

            if (!_whitelistTemp.ContainsKey(item.info.GetInstanceID()))
            {
                _whitelistTemp.Add(item.info.GetInstanceID(), property);
                timer.Once(config.InviteExpire, () =>
                {
                    if (_whitelistTemp.ContainsKey(item.info.GetInstanceID()))
                    {
                        _whitelistTemp.Remove(item.info.GetInstanceID());
                        SendReply(player, string.Format("[{0}] {1}", property.Name, lang.GetMessage("InviteExpired", this)));
                    }
                });
            }

            ShowUI(player, property);
        }

        [ConsoleCommand("property.buying.change")]
        private void PropertyBuyingChange(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            PropertyHouse property;
            PropertySubscriber subscriber;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (!propertyData.subscriber.TryGetValue(arg.Args[0], out subscriber))
                return;

            if (subscriber.PayerID != player.UserIDString)
            {
                if (property.Buying.Property != player.UserIDString)
                {
                    ShowUI(player, property, lang.GetMessage("NoOwner", this));
                    return;
                }
            }

            Item item = ItemManager.CreateByName("note");
            item.name = string.Format(lang.GetMessage("NoteTitleTenant", this), property.Name);
            item.text = string.Format(lang.GetMessage("NoteTextTenant", this), property.Name, player.displayName);
            player.GiveItem(item);

            if (!_whitelistPaymentTemp.ContainsKey(item.info.GetInstanceID()))
            {
                _whitelistPaymentTemp.Add(item.info.GetInstanceID(), property);
                timer.Once(config.InviteExpire, () =>
                {
                    if (_whitelistPaymentTemp.ContainsKey(item.info.GetInstanceID()))
                    {
                        _whitelistPaymentTemp.Remove(item.info.GetInstanceID());
                        SendReply(player, string.Format("[{0}] {1}", property.Name, lang.GetMessage("InviteExpired", this)));
                    }
                });
            }

            ShowUI(player, property);
        }

        [ConsoleCommand("property.user.page")]
        private void PropertyChangePageCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs(2))
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            ShowUI(player, property, index: Convert.ToInt32(arg.Args[1]));
        }

        [ConsoleCommand("property.user.delete")]
        private void PropertyUserDeleteCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs(2))
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            if (property.Buying.Property != player.UserIDString)
                return;

            var user = property.Buying.Whitelist.Where(x => x.UserID == arg.Args[1]).FirstOrDefault();
            if (user != null)
                property.Buying.Whitelist.Remove(user);

            ShowUI(player, property);
            CodeLockManager(property, "single_remove_whitelist", Convert.ToUInt64(user.UserID));
        }
        #endregion

        #region Manager
        [ConsoleCommand("property.manager.closeui")]
        private void PropertyManagerCloseUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (_instancePropertyManagerUI.ContainsKey(player.userID))
                _instancePropertyManagerUI.Remove(player.userID);

            CuiHelper.DestroyUi(player, PropertyManagerParentUI);
        }

        [ConsoleCommand("property.manager.edit")]
        private void PropertyManagerAddingCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!HasPermission(player))
                return;

            if (_instanceEditingProperty.ContainsKey(player.userID))
            {
                _instanceEditingProperty.Remove(player.userID);
            }
            else
            {
                if (arg.HasArgs())
                {
                    PropertyHouse property;
                    if (propertyData.house.TryGetValue(arg.Args[0], out property))
                        _instanceEditingProperty.Add(player.userID, property);
                }
                else
                {
                    _instanceEditingProperty.Add(player.userID, new PropertyHouse());
                }
            }

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.edit.name")]
        private void PropertyManagerEditNameCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!HasPermission(player))
                return;

            string text = string.Join(" ", arg.Args);

            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(player.userID, out property))
                return;

            property.Name = text;
            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.edit.leasedprice")]
        private void PropertyManagerEditLeasedPriceCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!HasPermission(player))
                return;

            int price;
            if (!int.TryParse(string.Join(" ", arg.Args), out price))
                return;

            if (!PropertyEditPricing(player, "lease", price))
                return;

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.edit.buyingprice")]
        private void PropertyManagerEditPriceCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!HasPermission(player))
                return;

            int price;
            if (!int.TryParse(string.Join(" ", arg.Args), out price))
                return;

            if (!PropertyEditPricing(player, "buy", price))
                return;

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.door.delete")]
        private void PropertyManagerDoorDeleteCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs(1))
                return;

            if (!HasPermission(player))
                return;

            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(player.userID, out property))
                return;

            property.CodeLock.Remove(Convert.ToUInt64(arg.Args[0]));

            BaseEntity entity = BaseEntity.saveList.Where(x => x.OwnerID == Convert.ToUInt64(arg.Args[0])).FirstOrDefault();
            if (entity != null)
                entity.Kill();

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.delete")]
        private void PropertyManagerDeleteCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!arg.HasArgs())
                return;

            if (!HasPermission(player))
                return;

            PropertyHouse property;
            if (!propertyData.house.TryGetValue(arg.Args[0], out property))
                return;

            CodeLockManager(property, "kill");
            propertyData.house.Remove(arg.Args[0]);
            if (propertyData.subscriber.ContainsKey(property.Mailbox.ToString()))
                propertyData.subscriber.Remove(property.Mailbox.ToString());

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.page")]
        private void PropertyManagerChangePageCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            string page;
            if (!_instancePropertyManagerUI.TryGetValue(player.userID, out page))
                return;

            if (!arg.HasArgs(1))
                return;

            if (!HasPermission(player))
                return;

            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(player.userID, out property))
                return;

            _instancePropertyManagerUI[player.userID] = arg.Args[0];
            PropertyManagerUI(player, arg.Args[0]);
        }

        [ConsoleCommand("property.manager.save")]
        private void PropertyManagerSaveCommandUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            if (!_instancePropertyManagerUI.ContainsKey(player.userID))
                return;

            if (!HasPermission(player))
                return;

            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(player.userID, out property))
                return;

            if (property.Name == null || property.Name.Length < 2)
            {
                ShowPropertyUI(player, lang.GetMessage("Error_Title", this));
                return;
            }

            if (property.Buying.Price == -1)
            {
                ShowPropertyUI(player, lang.GetMessage("Error_PriceSale", this));
                return;
            }

            if (property.Buying.Leased == -1)
            {
                ShowPropertyUI(player, lang.GetMessage("Error_PriceRental", this));
                return;
            }

            if (property.CodeLock.Count < 1)
            {
                ShowPropertyUI(player, lang.GetMessage("Error_CodeLock", this));
                return;
            }

            if (property.Mailbox == 0)
            {
                ShowPropertyUI(player, lang.GetMessage("Error_Mailbox", this));
                return;
            }

            string mailboxID = property.Mailbox.ToString();
            if (propertyData.house.ContainsKey(mailboxID))
            {
                propertyData.house[mailboxID] = property;
            }
            else
            {
                property.Id = new System.Random().Next(0, 99999).ToString();
                propertyData.house.Add(mailboxID, property);
            }

            foreach (var item in property.CodeLock)
            {
                var code = BaseEntity.FindObjectsOfType<CodeLock>().Where(x => x.OwnerID == item).FirstOrDefault();
                if (code != null)
                    code.SetFlag(BaseEntity.Flags.Locked, true);
            }
            _instanceEditingProperty.Remove(player.userID);

            ShowPropertyUI(player);
        }

        [ConsoleCommand("property.manager.indexpage")]
        private void PropertyManagerChangePage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
                return;

            string page;
            if (!_instancePropertyManagerUI.TryGetValue(player.userID, out page))
                return;

            if (!arg.HasArgs())
                return;

            if (!HasPermission(player))
                return;

            int index;
            if (!int.TryParse(arg.Args[0], out index))
                return;

            PropertyManagerUI(player, _instancePropertyManagerUI[player.userID], index: index);
        }
        #endregion
        #endregion

        #region Chat Command
        [ChatCommand("property")]
        void PropertyManagerCommand(BasePlayer player) => ShowPropertyUI(player);
        #endregion

        #region UI
        #region General
        void PropertyBuyingUI(BasePlayer player, PropertyHouse property, string page = "home", string error = "", int index = 0)
        {
            CuiHelper.DestroyUi(player, PropertyBuyingParentUI);

            var elements = new CuiElementContainer();

            var panel = elements.Add(GuiHelper.CreatePanel("0.5 0", "0.5 0", "0 0 0 0", false, OffsetMin: "192 350", OffsetMax: "572 650"), "Overlay", PropertyBuyingParentUI);
            GuiHelper.CreateLabel(ref elements, property.Name, panel, "0 0.89", "1 1", alignement: TextAnchor.MiddleLeft, fontSize: 20);

            var header_bar = elements.Add(GuiHelper.CreatePanel("0 0", "0 0", "1 0.96 0.88 0.15", OffsetMin: "0 240", OffsetMax: "380 265"), panel);
            GuiHelper.CreateLabel(ref elements, "Property buying".ToUpper(), header_bar, "0.02 0", "1 1", alignement: TextAnchor.MiddleLeft, fontSize: 13);
            GuiHelper.CreateLabel(ref elements, string.Format("V{0}", this.Version), header_bar, "0 0", "0.98 1", alignement: TextAnchor.MiddleRight, fontSize: 13);

            Dictionary<string, string> nav_buttons = new Dictionary<string, string>();
            nav_buttons.Add(lang.GetMessage("NavButtonHome", this), "home");
            nav_buttons.Add(lang.GetMessage("NavButtonUsers", this), "users");

            if (player.UserIDString == property.Buying.Property)
                nav_buttons.Add(lang.GetMessage("NavButtonCommands", this), "commands");

            double b = 1.0 / nav_buttons.Count;
            double minX = 0.0;
            foreach (var button in nav_buttons)
            {
                double maxX = minX + b;
                double color = 0.5;
                if (button.Value == page)
                    color = 0.6;

                GuiHelper.CreateButton(ref elements, panel, button.Key.ToUpper(), $"property.page {button.Value} {property.Mailbox}", $"{minX} 0.68", $"{maxX} 0.78", color: $"0 0 0 {color}");
                minX += b;
            }

            if (page == "home")
                PropertyBuyingHomeUI(ref elements, player, property, panel);

            if (page == "users")
                PropertyBuyingUsersUI(ref elements, player, property, panel, index);

            if (page == "commands")
                PropertyBuyingCommandsUI(ref elements, property, panel);

            if (error != "")
                GuiHelper.CreateLabel(ref elements, error, panel, "0 0", "1 0.18", alignement: TextAnchor.UpperLeft, color: "1 0 0 1", fontSize: 12);

            CuiHelper.AddUi(player, elements);
        }
        void PropertyBuyingHomeUI(ref CuiElementContainer elements, BasePlayer player, PropertyHouse property, string parent)
        {
            string card_first_top_title = lang.GetMessage("BuyedTitle", this);
            string card_first_top_text = $"{property.Buying.Price}$";

            string card_second_top_title = lang.GetMessage("LeaseTitle", this);
            string card_second_top_text = $"{property.Buying.Leased}$ / <size=18>{config.TimeForPayLeased}</size>";

            // Card
            var card_first = elements.Add(GuiHelper.CreatePanel("0 0.45", "1 0.65", "1 0.96 0.88 0.15"), parent);
            var card_second = elements.Add(GuiHelper.CreatePanel("0 0.2", "1 0.4", "1 0.96 0.88 0.15"), parent);

            if (property.Buying.Property != "")
            {
                // The property is selling
                card_first_top_title = lang.GetMessage("BelongsHas", this);
                card_second_top_title = lang.GetMessage("SellingProperty", this);
                card_second_top_text = string.Format("{0}$", property.Buying.PurchaseAmount / config.DivisionSelling);

                var user = property.Buying.Whitelist.Find(x => x.UserID == property.Buying.Property);
                if (user != null)
                    card_first_top_text = user.Name;


                if (property.Buying.Property == player.UserIDString || property.Buying.Payer == player.UserIDString)
                {
                    PropertySubscriber subscriber;
                    if (propertyData.subscriber.TryGetValue(property.Mailbox.ToString(), out subscriber))
                    {
                        if (subscriber.SubscriberAttempt != 0)
                            GuiHelper.CreateButton(ref elements, card_first, $"Pay {property.Buying.PurchaseAmount * subscriber.SubscriberAttempt}$".ToUpper(), $"property.leased.pay {property.Mailbox}", "0.7 0.25", "0.95 0.75", color: GuiHelper.HexToRGBA("#FF8408", 0.5f), size: 12);

                        TimeSpan diff = TransformStringToAddDate(subscriber.SubscriberDate) - DateTime.Now;
                        GuiHelper.CreateLabel(ref elements, string.Format("{0}: {1}h {2}m {3}s", lang.GetMessage("NextPayments", this).ToUpper(), diff.Hours, diff.Minutes, diff.Seconds), parent, "0.5 0", "1 0.18", alignement: TextAnchor.UpperRight, fontSize: 15);
                    }
                }

                // Second card selling button
                if (property.Buying.Property == player.UserIDString || property.Buying.Payer == player.UserIDString)
                    GuiHelper.CreateButton(ref elements, card_second, lang.GetMessage("SellButton", this).ToUpper(), $"property.sell {property.Mailbox}", "0.75 0.25", "0.95 0.75", color: GuiHelper.HexToRGBA("#DB2B30", 0.5f), size: 12);
            }
            else
            {
                // Buy Button
                GuiHelper.CreateButton(ref elements, card_first, lang.GetMessage("BuyButton", this).ToUpper(), $"property.buy {property.Mailbox}", "0.75 0.25", "0.95 0.75", color: GuiHelper.HexToRGBA("#6e8743", 0.5f), size: 12);

                // Leased Button
                GuiHelper.CreateButton(ref elements, card_second, lang.GetMessage("LeasedButton", this).ToUpper(), $"property.lease {property.Mailbox}", "0.75 0.25", "0.95 0.75", color: GuiHelper.HexToRGBA("#6e8743", 0.5f), size: 12);
            }

            // First Card
            GuiHelper.CreateLabel(ref elements, card_first_top_title.ToUpper(), card_first, "0.03 0", "0.7 0.8", alignement: TextAnchor.UpperLeft, fontSize: 10);
            GuiHelper.CreateLabel(ref elements, card_first_top_text.ToUpper(), card_first, "0.03 0", "0.7 0.6", alignement: TextAnchor.UpperLeft, fontSize: 20);

            // Second card
            GuiHelper.CreateLabel(ref elements, card_second_top_title.ToUpper(), card_second, "0.03 0", "0.7 0.8", alignement: TextAnchor.UpperLeft, fontSize: 10);
            GuiHelper.CreateLabel(ref elements, card_second_top_text, card_second, "0.03 0", "0.7 0.6", alignement: TextAnchor.UpperLeft, fontSize: 20);
        }
        void PropertyBuyingUsersUI(ref CuiElementContainer elements, BasePlayer player, PropertyHouse property, string parent, int index = 0)
        {
            double maxY = 0.65;
            double minX = 0;

            int i = 1;

            // MAX 6
            for (int x = index; x < property.Buying.Whitelist.Count(); x++)
            {
                if (x < index + 6)
                {
                    var data = property.Buying.Whitelist[x];

                    if (data.UserID != property.Buying.Property)
                    {
                        double maxX = minX + 0.48;
                        double minY = maxY - 0.15;

                        var card_player = elements.Add(GuiHelper.CreatePanel($"{minX} {minY}", $"{maxX} {maxY}", "1 0.96 0.88 0.15"), parent);
                        GuiHelper.CreateLabel(ref elements, data.Name, card_player, "0.05 0", "0.7 1", fontSize: 15, alignement: TextAnchor.MiddleLeft);

                        if (player.UserIDString == property.Buying.Property)
                            GuiHelper.CreateButton(ref elements, card_player, "X", $"property.user.delete {property.Mailbox} {data.UserID}", "0.8 0.2", "0.95 0.8", color: "1 0 0 1");

                        if (i == 2)
                        {
                            i = 1;
                            minX = 0;
                            maxY = maxY - 0.16;
                        }
                        else
                        {
                            minX += 0.52;
                            i++;
                        }
                    }
                }
            }

            if (index != 0)
                GuiHelper.CreateButton(ref elements, parent, lang.GetMessage("PreviousPage", this).ToUpper(), $"property.user.page {property.Mailbox} {index - 6}", "0 0.05", "0.45 0.15", color: GuiHelper.HexToRGBA("#6e8743", 1f));

            if (index < property.Buying.Whitelist.Count())
                GuiHelper.CreateButton(ref elements, parent, lang.GetMessage("NextPage", this).ToUpper(), $"property.user.page {property.Mailbox} {index + 6}", "0.55 0.05", "1 0.15", color: GuiHelper.HexToRGBA("#6e8743", 1f));
        }
        void PropertyBuyingCommandsUI(ref CuiElementContainer elements, PropertyHouse property, string parent)
        {
            GuiHelper.CreateButton(ref elements, parent, lang.GetMessage("AddTenantButton", this), $"property.user.add {property.Mailbox}", "0 0.5", "1 0.65");

            PropertySubscriber subscriber;
            if (propertyData.subscriber.TryGetValue(property.Mailbox.ToString(), out subscriber))
                GuiHelper.CreateButton(ref elements, parent, lang.GetMessage("ChangePayerButton", this), $"property.buying.change {property.Mailbox}", "0 0.33", "1 0.48");
        }
        void PropertyDestroyUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, PropertyBuyingParentUI);
            if (_instancePropertyUI.ContainsKey(player.userID))
                _instancePropertyUI.Remove(player.userID);
        }
        void ShowUI(BasePlayer player, PropertyHouse property, string error = "", int index = 0)
        {
            string page_target = "home";
            string page;

            if (!_instancePropertyUI.TryGetValue(player.userID, out page))
            {
                _instancePropertyUI.Add(player.userID, page_target);
            }
            else
            {
                page_target = page;
            }

            PropertyBuyingUI(player, property, page_target, error, index);
        }
        #endregion

        #region Manager
        void PropertyManagerUI(BasePlayer player, string page = "door", string error = "", int index = 0)
        {
            CuiHelper.DestroyUi(player, PropertyManagerParentUI);

            if (!HasPermission(player))
                return;

            var elements = new CuiElementContainer();
            var blur_main = elements.Add(new CuiPanel
            {
                Image =
                    {
                        Color = "0 0 0 0.5",
                        Sprite = "assets/content/materials/highlight.png",
                        Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                    },

                RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1",
                    },
                CursorEnabled = true,
            }, "Overlay", PropertyManagerParentUI);

            var panel = elements.Add(GuiHelper.CreatePanel("0.5 0.5", "0.5 0.5", "0 0 0 0", cursor: true, OffsetMin: "-170 -250", OffsetMax: "170 300"), blur_main);
            GuiHelper.CreateLabel(ref elements, "Property Manager".ToUpper(), panel, "0 0.9", "1 1", alignement: TextAnchor.MiddleLeft, fontSize: 30);
            var content = elements.Add(GuiHelper.CreatePanel("0 0", "1 0.9", GuiHelper.HexToRGBA("#262621", 0.5f)), panel);

            PropertyHouse property;
            if (_instanceEditingProperty.TryGetValue(player.userID, out property))
            {
                Dictionary<string, string> buttons_top = new Dictionary<string, string>();
                buttons_top.Add(lang.GetMessage("NavButtonDoor", this), "door");
                buttons_top.Add(lang.GetMessage("NavButtonPricing", this), "pricing");

                string input_text = "";
                if (property.Name != null)
                    input_text = property.Name;

                GuiHelper.CreateInputBox(ref elements, content, "property.manager.edit.name", lang.GetMessage("PropertyName", this).ToUpper(), "0.05 0.75", "0.95 0.85", textInput: input_text);
                GuiHelper.CreateButton(ref elements, content, lang.GetMessage("TopButtonCancel", this).ToUpper(), "property.manager.edit", "0.05 0.9", "0.45 0.97", color: "1 0 0 0.5");
                GuiHelper.CreateButton(ref elements, content, lang.GetMessage("TopButtonSave", this).ToUpper(), "property.manager.save", "0.5 0.9", "0.95 0.97", color: "0 0.8 0 0.5");

                double a = 1.0 / buttons_top.Count;
                double b = 0.05;
                foreach (var button in buttons_top)
                {
                    double color_opacity = 0.5;
                    double c = b + (a - 0.1);
                    if (page == button.Value)
                        color_opacity = 0.75;

                    GuiHelper.CreateButton(ref elements, content, button.Key.ToUpper(), $"property.manager.page {button.Value}", $"{b} 0.67", $"{c} 0.72", color: $"0 0 0 {color_opacity}", size: 10);
                    b += a;
                }

                switch (page)
                {
                    case "pricing":
                        PropertyManagerPricingUI(ref elements, property, content);
                        break;
                    case "door":
                        PropertyManagerDoorUI(ref elements, property, content);
                        break;
                }
                GuiHelper.CreateLabel(ref elements, error, content, "0.05 0.12", "0.95 0.2", "1 0 0 1", alignement: TextAnchor.MiddleLeft);
            }
            else
            {
                string btn_manager_text = "Add new";
                string btn_manager_color = GuiHelper.HexToRGBA("#6e8743", 0.5f);

                // List of property
                double c = 0.87;

                for (int i = index; i < propertyData.house.Count(); i++)
                {
                    if (i < index + 6)
                    {
                        var house = propertyData.house.ElementAt(i);

                        double b = c - 0.1;
                        GuiHelper.CreateButton(ref elements, content, $"    {house.Value.Name}", $"property.manager.edit {house.Key}", $"0.05 {b}", $"0.95 {c}", $"PropertyManager.Button.{i}.UI", alignement: TextAnchor.MiddleLeft, color: "1 0.96 0.88 0.15");
                        GuiHelper.CreateButton(ref elements, $"PropertyManager.Button.{i}.UI", "delete".ToUpper(), $"property.manager.delete {house.Key}", "0.75 0.25", "0.97 0.75", color: "1 0 0 0.5", size: 10);

                        c = c - 0.12;
                    }
                }

                if (index != 0)
                    GuiHelper.CreateButton(ref elements, content, lang.GetMessage("PreviousPage", this).ToUpper(), $"property.manager.indexpage {index - 6}", "0.05 0.105", "0.45 0.15", color: GuiHelper.HexToRGBA("#6e8743", 1f));

                if ((index + 6) <= propertyData.house.Count())
                    GuiHelper.CreateButton(ref elements, content, lang.GetMessage("NextPage", this).ToUpper(), $"property.manager.indexpage {index + 6}", "0.55 0.105", "0.95 0.15", color: GuiHelper.HexToRGBA("#6e8743", 1f));

                GuiHelper.CreateButton(ref elements, content, btn_manager_text.ToUpper(), "property.manager.edit", "0.05 0.9", "0.95 0.97", color: btn_manager_color);
            }

            // Utility Button
            GuiHelper.CreateButton(ref elements, content, lang.GetMessage("CloseButton", this), "property.manager.closeui", "0.05 0.02", "0.95 0.1");

            CuiHelper.AddUi(player, elements);
        }
        void PropertyManagerPricingUI(ref CuiElementContainer elements, PropertyHouse property, string parent)
        {
            GuiHelper.CreateInputBox(ref elements, parent, "property.manager.edit.leasedprice", "Leased pricing".ToUpper(), "0.05 0.54", "0.95 0.64", textInput: property.Buying.Leased.ToString());
            GuiHelper.CreateInputBox(ref elements, parent, "property.manager.edit.buyingprice", "Buying pricing".ToUpper(), "0.05 0.4", "0.95 0.5", textInput: property.Buying.Price.ToString());
        }
        void PropertyManagerDoorUI(ref CuiElementContainer elements, PropertyHouse property, string parent)
        {
            if (property.CodeLock.Count == 0)
            {
                GuiHelper.CreateLabel(ref elements, lang.GetMessage("NoCodeLockAdding", this), parent, "0.05 0.5", "0.95 0.65", alignement: TextAnchor.UpperLeft, fontSize: 13);
            }
            else
            {
                int i = 1;
                int count = 1;
                double x = 0.05;
                double d = 0.64;

                foreach (var codeLock in property.CodeLock)
                {
                    double m = x + 0.4;
                    double p = d - 0.04;

                    GuiHelper.CreateButton(ref elements, parent, string.Format(lang.GetMessage("DoorText", this), i).ToUpper(), $"property.manager.door.delete {codeLock}", $"{x} {p}", $"{m} {d}", size: 10);

                    if (count == 2)
                    {
                        count = 1;
                        d = d - 0.06;
                        x = 0.05;
                    }
                    else
                    {
                        x += 0.5;
                        count++;
                    }
                    i++;
                }
            }
        }
        void ShowPropertyUI(BasePlayer player, string error = "")
        {
            if (!HasPermission(player))
            {
                SendReply(player, lang.GetMessage("NoPermissions", this));
                return;
            }

            string page_target = "door";
            string page;
            if (!_instancePropertyManagerUI.TryGetValue(player.userID, out page))
            {
                _instancePropertyManagerUI.Add(player.userID, page_target);
            }
            else
            {
                page_target = page;
            }

            PropertyManagerUI(player, page_target, error);
        }
        #endregion
        #endregion

        #region CopyPaste
        void OnPasteFinished(List<BaseEntity> pastedEntities, string filename, IPlayer player)
        {
            System.Random rand = new System.Random();

            var mailbox = pastedEntities.Where(x => x.ShortPrefabName == "mailbox.deployed").FirstOrDefault();
            if (mailbox == null)
                return;

            PropertyHouse property;
            PropertyHouse newProperty = new PropertyHouse();
            if (!propertyData.house.TryGetValue(mailbox.OwnerID.ToString(), out property))
                return;

            ulong newMailBoxID = (ulong)rand.Next();

            newProperty.Id = new System.Random().Next(0, 99999).ToString();
            newProperty.Mailbox = newMailBoxID;
            newProperty.Name = $"{property.Name} {Convert.ToUInt64(new System.Random().Next(1, 999))}";

            // Buying Pricing
            newProperty.Buying.Price = property.Buying.Price;
            newProperty.Buying.Leased = property.Buying.Leased;

            // Update mailbox ID
            mailbox.OwnerID = newMailBoxID;

            // Update code lock
            foreach (var item in property.CodeLock)
            {
                var parentCodeLock = pastedEntities.Find(x => x.OwnerID == item);
                if (parentCodeLock != null)
                {
                    parentCodeLock.OwnerID = (ulong)rand.Next();

                    BaseEntity codeLock = parentCodeLock.GetSlot(BaseEntity.Slot.Lock);
                    if (codeLock != null)
                    {
                        codeLock.OwnerID = parentCodeLock.OwnerID;
                        codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                    }

                    newProperty.CodeLock.Add(parentCodeLock.OwnerID);
                }
            }

            propertyData.house.Add(newMailBoxID.ToString(), newProperty);

            var basePlayer = player.Object as BasePlayer;
            if (basePlayer != null)
                SendReply(basePlayer, $"<color={config.ColorNotification}>[Property Buying]</color> {string.Format(lang.GetMessage("CopyPasteSuccess", this), property.Name, newProperty.Name)}");
        }
        #endregion

        #region Functions
        void TimerPayment(string id, DateTime date)
        {
            if (!propertyData.subscriber.ContainsKey(id))
                return;

            TimeSpan diff = TransformStringToAddDate(date) - DateTime.Now;

            timer.Once(Convert.ToSingle(diff.TotalSeconds), () =>
            {
                PropertyHouse property;
                PropertySubscriber subscriber;
                if (!propertyData.house.TryGetValue(id, out property))
                    return;

                if (!propertyData.subscriber.TryGetValue(id, out subscriber))
                    return;

                int amount = subscriber.Amount;
                if (economicsPluginActive && config.PurchaseEconomics)
                {
                    double balance = (double)Economics.Call("Balance", subscriber.PayerID);
                    if (balance >= amount)
                    {
                        Economics.Call("Withdraw", subscriber.PayerID, Convert.ToDouble(amount));

                        BasePlayer player = BasePlayer.Find(subscriber.PayerID);
                        if (player != null)
                            SendReply(player, $"<color={config.ColorNotification}>[{property.Name}]</color> {string.Format(lang.GetMessage("WithdrawMoney", this), subscriber.Amount)}");
                    }
                    else
                    {
                        propertyData.subscriber[id].SubscriberAttempt++;
                    }
                }
                else
                {
                    propertyData.subscriber[id].SubscriberAttempt++;

                    BasePlayer player = BasePlayer.Find(subscriber.PayerID);
                    if (player != null)
                        SendReply(player, $"<color={config.ColorNotification}>[{property.Name}]</color> {lang.GetMessage("WaitingPayments", this)}");
                }

                DateTime now = DateTime.Now;
                propertyData.subscriber[id].SubscriberDate = now;
                TimerPayment(id, now);
            });
        }
        bool PropertyEditPricing(BasePlayer player, string type, int price)
        {
            PropertyHouse property;
            if (!_instanceEditingProperty.TryGetValue(player.userID, out property))
                return false;

            if (type == "buy")
                property.Buying.Price = price;

            if (type == "lease")
                property.Buying.Leased = price;

            return true;
        }
        bool HasPermission(BasePlayer player)
        {
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PropertyManagerPermissions))
                return true;
            return false;
        }
        void CodeLockManager(PropertyHouse property, string options, ulong playerID = 0)
        {
            foreach (var item in property.CodeLock)
            {
                var codeLock = BaseEntity.FindObjectsOfType<CodeLock>().Where(x => x.OwnerID == item).FirstOrDefault();
                if (codeLock != null)
                {
                    switch (options)
                    {
                        case "lock_whitelist":
                            codeLock.whitelistPlayers = new List<ulong>();
                            foreach (var player in property.Buying.Whitelist)
                                codeLock.whitelistPlayers.Add(Convert.ToUInt64(player.UserID));

                            codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                            break;
                        case "whitelist":
                            List<ulong> list = new List<ulong>();
                            foreach (var player in property.Buying.Whitelist)
                                list.Add(Convert.ToUInt64(player.UserID));
                            codeLock.whitelistPlayers = list;
                            break;
                        case "single_whitelist":
                            codeLock.whitelistPlayers.Add(playerID);
                            break;
                        case "single_remove_whitelist":
                            codeLock.whitelistPlayers.Remove(playerID);
                            break;
                        case "remove_all":
                            codeLock.whitelistPlayers = new List<ulong>();

                            var door = codeLock.GetParentEntity();
                            if (door != null)
                                door.SetFlag(BaseEntity.Flags.Open, false);
                            break;
                        case "lock":
                            codeLock.SetFlag(BaseEntity.Flags.Locked, true);
                            break;
                        case "kill":
                            codeLock.Kill();
                            break;
                    }
                }
            }
        }
        bool CheckPlayerBalancePay(BasePlayer player, int amount, string propertyName)
        {
            if (economicsPluginActive && config.PurchaseEconomics)
            {
                double balance = (double)Economics.Call("Balance", player.UserIDString);
                if (balance >= amount)
                {
                    Economics.Call("Withdraw", player.UserIDString, Convert.ToDouble(amount));
                    SendReply(player, string.Format("{0} {1}", $"<color={config.ColorNotification}>[{propertyName}]</color>", string.Format(lang.GetMessage("BuyEconomicsProperty", this), amount)));
                    return true;
                }
            }
            else
            {
                List<Item> collect = new List<Item>();
                int amountInventory = 0;

                foreach (var item in player.inventory.FindItemIDs(-932201673))
                    amountInventory = amountInventory + item.amount;

                if (amountInventory >= amount)
                {
                    player.inventory.Take(collect, -932201673, amount);
                    player.Command("note.inv", -932201673, -amount);
                    SendReply(player, string.Format("{0} {1}", $"<color={config.ColorNotification}>[{propertyName}]</color>", string.Format(lang.GetMessage("BuyProperty", this), amount)));
                    return true;
                }
            }
            return false;
        }
        DateTime TransformStringToAddDate(DateTime initial)
        {
            double timeAdding;
            string time = config.TimeForPayLeased;

            if (time.Contains("d"))
            {
                time = config.TimeForPayLeased.Replace("d", "");
                if (double.TryParse(time, out timeAdding))
                    return initial.AddDays(timeAdding);
            }
            if (time.Contains("h"))
            {
                time = config.TimeForPayLeased.Replace("h", "");
                if (double.TryParse(time, out timeAdding))
                    return initial.AddHours(timeAdding);
            }
            if (time.Contains("m"))
            {
                time = config.TimeForPayLeased.Replace("m", "");
                if (double.TryParse(time, out timeAdding))
                    return initial.AddMinutes(timeAdding);
            }
            if (time.Contains("s"))
            {
                time = config.TimeForPayLeased.Replace("s", "");
                if (double.TryParse(time, out timeAdding))
                    return initial.AddSeconds(timeAdding);
            }
            return initial;
        }
        void SellingProperty(PropertyHouse property)
        {
            property.Buying.Property = "";
            property.Buying.Payer = "";
            property.Buying.PurchaseAmount = 0;

            if (propertyData.subscriber.ContainsKey(property.Mailbox.ToString()))
                propertyData.subscriber.Remove(property.Mailbox.ToString());

            property.Buying.Whitelist = new List<PropertyWhitelist>();
            CodeLockManager(property, "remove_all");
        }
        void RefundThePlayer(BasePlayer player, PropertyHouse property)
        {
            int amount = property.Buying.PurchaseAmount / config.DivisionSelling;
            if (economicsPluginActive && config.PurchaseEconomics)
            {
                Economics.Call("Deposit", property.Buying.Payer.ToString(), Convert.ToDouble(amount));
                return;
            }
            if (amount != 0)
                player.GiveItem(ItemManager.CreateByItemID(-932201673, amount));
        }
        #endregion

        #region Localization
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["SellingProperty"] = "Selling the property",
                ["NoPermissions"] = "You don't have permission",
                ["NoOwner"] = "You are not the owner",
                ["NoFounds"] = "You don't have enough funds",
                ["PropertyAlreadyOwner"] = "This owner already belongs to someone",
                ["LeasedButton"] = "Leased",
                ["BuyButton"] = "Buy",
                ["CloseButton"] = "Close",
                ["SellButton"] = "Sell",
                ["LeaseTitle"] = "Lease the property",
                ["BuyedTitle"] = "Buyed the property",
                ["BelongsHas"] = "Belongs has",
                ["NavButtonHome"] = "Home",
                ["NavButtonUsers"] = "Users",
                ["AddTenantButton"] = "Added a new tenant",
                ["ChangePayerButton"] = "Change the payer",
                ["NavButtonCommands"] = "Commands",
                ["TopButtonCancel"] = "Cancel the edit",
                ["TopButtonSave"] = "Save",
                ["PropertyName"] = "Property Name",
                ["NavButtonDoor"] = "Door",
                ["NavButtonBuying"] = "Buying",
                ["NextPayments"] = "Next payments",
                ["NavButtonPricing"] = "Pricing",
                ["NextPage"] = "Next page",
                ["PreviousPage"] = "Previous page",
                ["DoorText"] = "Door #{0}",
                ["CodeLockAdding"] = "The lock code has been added to the \"{0}\"",
                ["MailboxSet"] = "The mailbox was set to \"{0}\"",
                ["NoCodeLockAdding"] = "No lock code was added, (give you a lock code to put it on the door)",
                ["NoteTitleTenant"] = "Added a new tenant '{0}'",
                ["NoteTextTenant"] = "You authorized to be added to the \"{0}\" property by {1}\n\n(left click to accept)\n(right click to decline)",
                ["InviteDecline"] = "You refused invitations",
                ["InviteAccept"] = "You accepted the invitations",
                ["InviteExpired"] = "Your invitations have expired",
                ["WithdrawMoney"] = "You were charged {0}$",
                ["WaitingPayments"] = "Your payment is waiting for you",
                ["BuyProperty"] = "You just paid {0}$",
                ["BuyEconomicsProperty"] = "A {0}$ payment has just been withdrawn from your account",
                ["Error_CodeLock"] = "Must have at least a lock code",
                ["Error_Mailbox"] = "He must have a mailbox",
                ["Error_PriceRental"] = "The rental price is not defined",
                ["Error_PriceSale"] = "The sale price is not defined",
                ["Error_Title"] = "It must have a minimum of 2 characters",
                ["CopyPasteSuccess"] = "\"{0}\" was copied under the name \"{1}\"",
                ["PurchaseLimit"] = "You have reached the purchase limit",
            }, this);
        }
        #endregion

        #region GuiHelper
        static class GuiHelper
        {
            public static void CreateImage(ref CuiElementContainer container, string parent, string AnchorMin, string AnchorMax, string imgPng, string color = "1.0 1.0 1.0 1.0")
            {
                var image = new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = parent,
                    Components = {
                        new CuiRawImageComponent {
                            Png = imgPng,
                            Color = color
                        },
                        new CuiRectTransformComponent {
                            AnchorMin = AnchorMin,
                            AnchorMax = AnchorMax,
                            OffsetMin = "0 0",
                            OffsetMax = "0 0"
                        }
                    }
                };
                container.Add(image);
            }
            public static void CreateButton(ref CuiElementContainer container, string panel, string text, string command, string AnchorMin, string AnchorMax, string name = "", string color = "0.31 0.31 0.31 1", int size = 14, string textColor = "1 1 1 1", TextAnchor alignement = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button =
                    {
                        Command = command,
                        Color = color
                    },
                    RectTransform =
                    {
                        AnchorMin = AnchorMin,
                        AnchorMax = AnchorMax,
                        OffsetMin = "0 0",
                        OffsetMax = "0 0"
                    },
                    Text =
                    {
                        Text = text,
                        FontSize = size,
                        Color = textColor,
                        Align = alignement
                    }
                }, panel, name);
            }
            public static void CreateLabel(ref CuiElementContainer container, string text, string panel, string AnchorMin, string AnchorMax, string color = "1 1 1 1", int fontSize = 15, TextAnchor alignement = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = text,
                        Align = alignement,
                        Color = color,
                        FontSize = fontSize
                    },
                    RectTransform = {
                        AnchorMin = AnchorMin,
                        AnchorMax = AnchorMax,
                        OffsetMin = "0 0",
                        OffsetMax = "0 0"
                    }
                }, panel, CuiHelper.GetGuid());
            }
            public static CuiPanel CreatePanel(string AnchorMin, string AnchorMax, string color = "1 1 1 1", bool cursor = false, string OffsetMin = "0 0", string OffsetMax = "0 0")
            {
                CuiPanel panel = new CuiPanel
                {
                    Image =
                    {
                        Color = color
                    },

                    RectTransform =
                    {
                        AnchorMin = AnchorMin,
                        AnchorMax = AnchorMax,
                        OffsetMin = OffsetMin,
                        OffsetMax = OffsetMax
                    },
                    CursorEnabled = cursor,
                };
                return panel;
            }
            public static void CreateInput(ref CuiElementContainer container, string panel, string command, string AnchorMin, string AnchorMax, string color = "1 1 1 1", int fontSize = 13, int charsLimit = 40, bool password = false, string font = "robotocondensed-regular.ttf", TextAnchor alignement = TextAnchor.MiddleLeft)
            {
                var input = new CuiElement
                {
                    Name = "Input",
                    Parent = panel,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            CharsLimit = charsLimit,
                            Color = color,
                            IsPassword = password,
                            Command = command,
                            Font =font ,
                            FontSize = fontSize,
                            Align = alignement
                        },

                        new CuiRectTransformComponent
                        {
                            AnchorMin = AnchorMin,
                            AnchorMax = AnchorMax,
                            OffsetMin = "0 0",
                            OffsetMax = "0 0"
                        }
                    }
                };
                container.Add(input);
            }
            public static void CreateInputBox(ref CuiElementContainer container, string parent, string command, string text, string AnchorMin, string AnchorMax, int textSize = 15, TextAnchor textAlignement = TextAnchor.UpperLeft, string textInput = "")
            {
                var input_box = container.Add(CreatePanel(AnchorMin, AnchorMax, "0 0 0 0"), parent);
                CreateLabel(ref container, text, input_box, "0 0", "1 1", alignement: textAlignement, fontSize: textSize);

                var input = container.Add(CreatePanel("0 0", "1 0.6", "0 0 0 1"), input_box);
                CreateLabel(ref container, textInput, input, "0.01 0", "1 1", color: "1 1 1 0.4", alignement: TextAnchor.MiddleLeft);
                CreateInput(ref container, input, command, "0.01 0", "1 1");
            }
            public static void CreateButtonImage(ref CuiElementContainer container, string parent, string command, string AnchorMin, string AnchorMax, string imgPng, string color = "0 0 0 0", string colorBg = "0 0 0 0")
            {
                GuiHelper.CreateButton(ref container, parent, "", command, AnchorMin, AnchorMax, "buttonImg", "0 0 0 0");
                GuiHelper.CreateImage(ref container, "buttonImg", "0 0", "1 1", imgPng);
            }
            public static string HexToRGBA(string hex, float alpha)
            {
                if (hex.StartsWith("#"))
                    hex = hex.TrimStart('#');

                int red = int.Parse(hex.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hex.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hex.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }
        #endregion
    }
}