using System.Data.Common;
using Grpc.Core;
using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.LuaFunctions;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Server.World.Service;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Maple2.Server.Game.Manager;

public sealed class BlackMarketManager {
    private readonly GameSession session;

    private readonly ILogger logger = Log.Logger.ForContext<BlackMarketManager>();

    public BlackMarketManager(GameSession session) {
        this.session = session;
    }

    public void LoadMyListings() {
        using GameStorage.Request db = session.GameStorage.Context();
        ICollection<BlackMarketListing> entries = db.GetBlackMarketListings(session.CharacterId).ToList();

        session.Send(BlackMarketPacket.MyListings(entries));
    }

    public void Add(long itemUid, long price, int quantity) {
        BlackMarketRegistration? registration = null;
        lock (session.Item) {
            Item? item = session.Item.Inventory.Get(itemUid);
            if (item == null) {
                session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_register_not_exist_in_inven));
                return;
            }

            if (quantity <= 0 || item.Amount < quantity || price <= 0) {
                session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_lack_sale_count));
                return;
            }
            if (item.IsExpired()) {
                return;
            }

            float depositPercent = Lua.CalcBlackMarketRegisterDepositPercent();
            long depositFee;
            try {
                long total = checked(price * quantity);
                depositFee = Lua.CalcBlackMarketRegisterDeposit(checked((long) (total * (decimal) depositPercent)));
            } catch (OverflowException) {
                session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_invalid_sale_count));
                return;
            }

            if (depositFee < 0 || session.Currency.CanAddMeso(-depositFee) != -depositFee) {
                session.Send(BlackMarketPacket.Error(BlackMarketError.s_err_lack_meso));
                return;
            }
            if (SaveBeforeTransaction()) {
                try {
                    using GameStorage.Request db = session.GameStorage.Context();
                    BlackMarketError error = db.RegisterBlackMarketListing(session.Player.Value, item, quantity, price, depositFee, out registration);
                    if (error != BlackMarketError.none) {
                        session.Send(BlackMarketPacket.Error(error));
                        return;
                    }
                    if (registration == null) {
                        throw new InvalidOperationException("A committed black-market registration must include its listing receipt.");
                    }
                    session.Item.Inventory.ApplyRemoved(item, quantity);
                    session.Player.Value.Currency.Meso = registration.SellerMeso;
                    session.Player.Value.Character.LastModified = registration.CharacterLastModified;
                } catch (Exception ex) {
                    logger.Error(ex, "Failed black market registration for item {ItemUid}; disconnecting stale or uncertain seller", itemUid);
                    session.AbortPersistence("Black-market registration or its in-memory receipt could not be confirmed.");
                    registration = null;
                }
            }
        }

        if (registration == null) {
            session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_close));
            session.Disconnect();
            return;
        }
        session.Currency.NotifyChanges(meso: 0);
        session.Send(BlackMarketPacket.Add(registration.Listing));
        RefreshWorld(new BlackMarketRequest {
            Add = new BlackMarketRequest.Types.Add {
                ListingId = registration.Listing.Id,
            },
        });
    }

    public void Remove(long listingId) {
        lock (session.Item) {
            if (session.PersistenceAborted) {
                return;
            }
            try {
                using GameStorage.Request db = session.GameStorage.Context();
                BlackMarketError error = db.CancelBlackMarketListing(session.AccountId, session.CharacterId, listingId);
                if (error != BlackMarketError.none) {
                    session.Send(BlackMarketPacket.Error(error));
                    return;
                }
            } catch (Exception ex) when (ex is DbException or DbUpdateException) {
                logger.Error(ex, "Failed to cancel black market listing {ListingId} for {CharacterId}", listingId, session.CharacterId);
                session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_close));
                return;
            }
        }

        session.Send(BlackMarketPacket.Remove(listingId));
        RefreshWorld(new BlackMarketRequest {
            Remove = new BlackMarketRequest.Types.Remove {
                ListingId = listingId,
            },
        });
        session.Mail.Notify(true);
    }

    public void Purchase(long listingId, int quantity) {
        if (quantity <= 0) {
            session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_purchase_count));
            return;
        }
        BlackMarketPurchase? purchase = null;
        lock (session.Item) {
            if (SaveBeforeTransaction()) {
                try {
                    using GameStorage.Request db = session.GameStorage.Context();
                    BlackMarketError error = db.PurchaseBlackMarketListing(session.Player.Value, listingId, quantity,
                        Lua.CalcBlackMarketCostRate(), out purchase);
                    if (error != BlackMarketError.none) {
                        session.Send(BlackMarketPacket.Error(error));
                        return;
                    }
                    if (purchase == null) {
                        throw new InvalidOperationException("A committed black-market purchase must include its payment receipt.");
                    }
                    session.Player.Value.Currency.Meso = purchase.BuyerMeso;
                    session.Player.Value.Character.LastModified = purchase.CharacterLastModified;
                } catch (Exception ex) {
                    logger.Error(ex, "Failed black market purchase {ListingId} for {CharacterId}; disconnecting stale or uncertain buyer",
                        listingId, session.CharacterId);
                    session.AbortPersistence("Black-market purchase or its in-memory receipt could not be confirmed.");
                    purchase = null;
                }
            }
        }

        if (purchase == null) {
            session.Send(BlackMarketPacket.Error(BlackMarketError.s_blackmarket_error_close));
            session.Disconnect();
            return;
        }
        session.Currency.NotifyChanges(meso: 0);
        session.Send(BlackMarketPacket.Purchase(listingId, quantity));
        RefreshWorld(new BlackMarketRequest {
            Purchase = new BlackMarketRequest.Types.Purchase {
                ListingId = listingId,
                SellerId = purchase.SellerMail.ReceiverId,
            },
        });
        session.Mail.Notify(true);
        try {
            session.World.MailNotification(new MailNotificationRequest {
                CharacterId = purchase.SellerMail.ReceiverId,
                MailId = purchase.SellerMail.Id,
            });
        } catch (RpcException ex) {
            logger.Warning(ex, "Committed black market sale mail {MailId} could not notify seller {CharacterId}",
                purchase.SellerMail.Id, purchase.SellerMail.ReceiverId);
        }
    }

    private bool SaveBeforeTransaction() {
        // Persist the inventory changes behind any pending earnings before making their expenditure durable.
        return session.SessionSave();
    }

    private void RefreshWorld(BlackMarketRequest request) {
        try {
            BlackMarketResponse response = session.World.BlackMarket(request);
            if (response.Error != 0) {
                logger.Warning("Black market cache refresh rejected after commit: {Error}", (BlackMarketError) response.Error);
            }
        } catch (RpcException ex) {
            logger.Warning(ex, "Black market cache refresh failed after commit");
        }
    }

    public void Preview(int itemId, int rarity) {
        // We're not checking if the item is in the inventory or not as this is just for previewing. We'll check in Add.
        if (!session.ItemMetadata.TryGet(itemId, out ItemMetadata? metadata)) {
            return;
        }
        var type = new ItemType(metadata.Id);
        long sellPrice = Core.Formulas.Shop.SellPrice(metadata, type, rarity);

        session.Send(BlackMarketPacket.Preview(itemId, rarity, sellPrice));
    }
}
