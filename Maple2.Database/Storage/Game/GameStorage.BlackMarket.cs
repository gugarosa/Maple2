using System.Data;
using Maple2.Database.Extensions;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Maple2.Database.Storage;

public sealed record BlackMarketPurchase(Mail SellerMail, long BuyerMeso, DateTime CharacterLastModified);
public sealed record BlackMarketRegistration(BlackMarketListing Listing, long SellerMeso, DateTime CharacterLastModified);

public partial class GameStorage {
    public partial class Request {
        public BlackMarketError RegisterBlackMarketListing(Player seller, Item offered, int quantity, long price, long deposit,
                                                          out BlackMarketRegistration? registration) {
            registration = null;
            if (quantity <= 0 || quantity > offered.Amount || price <= 0 || deposit < 0) {
                return BlackMarketError.s_blackmarket_error_invalid_sale_count;
            }
            try {
                _ = checked(price * quantity);
            } catch (OverflowException) {
                return BlackMarketError.s_blackmarket_error_invalid_sale_count;
            }
            if (seller.Currency.Meso < deposit) {
                return BlackMarketError.s_err_lack_meso;
            }

            using IDbContextTransaction transaction = BeginBlackMarketTransaction();
            try {
                Model.Item? item = Context.Item
                    .FromSqlInterpolated($"SELECT * FROM `item` WHERE `Id` = {offered.Uid} FOR UPDATE")
                    .AsTracking().AsEnumerable().SingleOrDefault();
                if (item == null || item.OwnerId != seller.Character.Id || item.Amount != offered.Amount ||
                    item.Group != ItemGroup.Default || ToItem(item) == null) {
                    throw new DbUpdateConcurrencyException("The offered black-market item is no longer in the seller's inventory.");
                }
                if (item.ExpiryTime != default && item.ExpiryTime <= DateTime.Now) {
                    return BlackMarketError.s_blackmarket_error_buy_expired;
                }

                long meso = seller.Currency.Meso - deposit;
                DateTime version = SaveBlackMarketMeso(seller, meso);
                Model.Item listedItem = item;
                if (quantity < item.Amount) {
                    listedItem = (Model.Item) Context.Entry(item).CurrentValues.ToObject();
                    listedItem.Id = 0;
                    listedItem.Amount = quantity;
                    listedItem.LastModified = default;
                    item.Amount -= quantity;
                    Context.Item.Add(listedItem);
                    Context.SaveChanges();
                }
                var model = new Model.BlackMarketListing {
                    ItemUid = listedItem.Id,
                    AccountId = seller.Account.Id,
                    CharacterId = seller.Character.Id,
                    Price = price,
                    Quantity = quantity,
                    Deposit = deposit,
                    ExpiryTime = DateTime.Now.AddDays(game.serverTableMetadata.ConstantsTable.BlackMarketSellEndDay),
                };
                Context.BlackMarketListing.Add(model);
                Context.SaveChanges();
                listedItem.OwnerId = model.Id;
                listedItem.Slot = -1;
                listedItem.Group = ItemGroup.Default;
                Context.SaveChanges();
                BlackMarketListing created = model.Convert(ToItem(listedItem)!);
                transaction.Commit();
                registration = new BlackMarketRegistration(created, meso, version);
                return BlackMarketError.none;
            } finally {
                Context.ChangeTracker.Clear();
            }
        }

        public IEnumerable<BlackMarketListing> GetBlackMarketListings(long characterId) {
            return Context.BlackMarketListing.Where(listing => listing.CharacterId == characterId)
                .ToArray().Select(ToBlackMarketingListing).OfType<BlackMarketListing>();
        }

        public BlackMarketListing? GetBlackMarketListing(long listingId) {
            return ToBlackMarketingListing(Context.BlackMarketListing.AsNoTracking().SingleOrDefault(listing => listing.Id == listingId));
        }

        public IEnumerable<BlackMarketListing> GetBlackMarketListings(params long[] listingIds) {
            return Context.BlackMarketListing.Where(listing => listingIds.Contains(listing.Id))
                .ToArray().Select(ToBlackMarketingListing).OfType<BlackMarketListing>();
        }

        public IEnumerable<BlackMarketListing> GetAllBlackMarketListings() {
            return Context.BlackMarketListing.ToArray().Select(ToBlackMarketingListing).OfType<BlackMarketListing>();
        }

        public BlackMarketError PurchaseBlackMarketListing(Player buyer, long listingId, int quantity, float costRate,
                                                           out BlackMarketPurchase? purchase) {
            purchase = null;
            if (quantity <= 0) {
                return BlackMarketError.s_blackmarket_error_purchase_count;
            }
            if (!float.IsFinite(costRate) || costRate is < 0 or > 1) {
                throw new ArgumentOutOfRangeException(nameof(costRate));
            }

            using IDbContextTransaction transaction = BeginBlackMarketTransaction();
            try {
                (Model.BlackMarketListing? listing, Model.Item? item) = LockBlackMarketListing(listingId);
                if (listing == null || item == null) {
                    return BlackMarketError.s_err_lack_itemcount;
                }
                if (listing.AccountId == buyer.Account.Id) {
                    return BlackMarketError.s_blackmarket_error_cannot_buy_ownProduct;
                }
                if (listing.ExpiryTime <= DateTime.Now || (item.ExpiryTime != default && item.ExpiryTime <= DateTime.Now)) {
                    return BlackMarketError.s_blackmarket_error_buy_expired;
                }
                if (listing.Quantity < quantity) {
                    return BlackMarketError.s_blackmarket_error_purchase_count;
                }
                long total;
                try {
                    total = checked(listing.Price * quantity);
                } catch (OverflowException) {
                    return BlackMarketError.s_blackmarket_error_purchase_count;
                }
                if (buyer.Currency.Meso < total) {
                    return BlackMarketError.s_err_lack_meso;
                }

                Model.Account? seller = Context.Account.AsNoTracking().SingleOrDefault(account => account.Id == listing.AccountId);
                if (seller == null || !Context.Character.Any(character => character.Id == listing.CharacterId && character.AccountId == seller.Id)) {
                    return BlackMarketError.s_blackmarket_error_close;
                }
                bool premium = seller.PremiumTime > DateTime.Now.ToEpochSeconds();
                long tax = checked((long) ((decimal) costRate * total));
                long fee = premium ? checked((long) (tax * (decimal) Constant.BlackMarketPremiumClubDiscount)) : tax;
                long revenue;
                bool soldOut = listing.Quantity == quantity;
                try {
                    revenue = checked(total - fee + (soldOut ? listing.Deposit : 0));
                } catch (OverflowException) {
                    return BlackMarketError.s_blackmarket_error_close;
                }

                long meso = buyer.Currency.Meso - total;
                DateTime version = SaveBlackMarketMeso(buyer, meso);

                BlackMarketListing value = listing.Convert(ToItem(item)!);
                value.Quantity -= quantity;
                Model.Mail buyerMail = BlackMarketBuyerMail(value, buyer.Character.Id, quantity, total);
                Model.Mail sellerMail = BlackMarketSellerMail(value, quantity, total, costRate, tax, fee, revenue, premium);
                Context.Mail.AddRange(buyerMail, sellerMail);
                Context.SaveChanges();

                if (soldOut) {
                    item.OwnerId = buyerMail.Id;
                    item.Slot = -1;
                    item.Group = ItemGroup.Default;
                    Context.BlackMarketListing.Remove(listing);
                } else {
                    Model.Item attachment = (Model.Item) Context.Entry(item).CurrentValues.ToObject();
                    attachment.Id = 0;
                    attachment.OwnerId = buyerMail.Id;
                    attachment.Amount = quantity;
                    attachment.Slot = -1;
                    attachment.Group = ItemGroup.Default;
                    attachment.LastModified = default;
                    Context.Item.Add(attachment);
                    item.Amount -= quantity;
                    listing.Quantity -= quantity;
                }
                Context.SaveChanges();
                transaction.Commit();
                purchase = new BlackMarketPurchase(sellerMail, meso, version);
                return BlackMarketError.none;
            } finally {
                Context.ChangeTracker.Clear();
            }
        }

        public BlackMarketError CancelBlackMarketListing(long accountId, long characterId, long listingId) {
            using IDbContextTransaction transaction = BeginBlackMarketTransaction();
            try {
                (Model.BlackMarketListing? listing, Model.Item? item) = LockBlackMarketListing(listingId);
                if (listing == null || item == null || listing.AccountId != accountId || listing.CharacterId != characterId) {
                    return BlackMarketError.s_blackmarket_error_close;
                }

                long total;
                try {
                    total = checked(listing.Price * listing.Quantity);
                } catch (OverflowException) {
                    return BlackMarketError.s_blackmarket_error_close;
                }
                bool expired = listing.ExpiryTime <= DateTime.Now;
                long deposit = expired ? listing.Deposit : 0;
                var mail = new Mail(game.serverTableMetadata.ConstantsTable.MailExpiryDays) {
                    ReceiverId = characterId,
                    Type = MailType.BlackMarketListingCancel,
                    TitleArgs = [("item", $"{item.ItemId}")],
                    ContentArgs = [
                        ("key", $"{(expired ? StringCode.s_blackmarket_mail_to_cancel_expired : StringCode.s_blackmarket_mail_to_cancel_direct)}"),
                        ("item", $"{item.ItemId}"),
                        ("str", $"{listing.Quantity}"),
                        ("money", $"{total}"),
                        ("money", $"{listing.Price}"),
                        ("money", $"{deposit}"),
                    ],
                    Meso = deposit,
                };
                mail.SetTitle(StringCode.s_blackmarket_mail_to_cancel_title);
                mail.SetContent(StringCode.s_blackmarket_mail_to_cancel_content);
                mail.SetSenderName(StringCode.s_blackmarket_mail_to_sender);
                Model.Mail model = mail;
                Context.Mail.Add(model);
                Context.SaveChanges();
                item.OwnerId = model.Id;
                item.Slot = -1;
                item.Group = ItemGroup.Default;
                Context.BlackMarketListing.Remove(listing);
                Context.SaveChanges();
                transaction.Commit();
                return BlackMarketError.none;
            } finally {
                Context.ChangeTracker.Clear();
            }
        }

        private IDbContextTransaction BeginBlackMarketTransaction() {
            if (HasFailed || Context.Database.CurrentTransaction != null || Context.ChangeTracker.HasChanges()) {
                throw new InvalidOperationException("Black-market operations require a clean, standalone storage request.");
            }
            Context.ChangeTracker.Clear();
            return Context.Database.BeginTransaction(IsolationLevel.ReadCommitted);
        }

        private DateTime SaveBlackMarketMeso(Player player, long meso) {
            int paid = Context.Database.ExecuteSqlInterpolated($"""
                UPDATE `character`
                SET `Currency` = JSON_SET(`Currency`, '$.Meso', {meso}),
                    `LastModified` = GREATEST(CURRENT_TIMESTAMP(6), DATE_ADD(`LastModified`, INTERVAL 1 MICROSECOND))
                WHERE `Id` = {player.Character.Id} AND `AccountId` = {player.Account.Id}
                    AND `LastModified` = {player.Character.LastModified}
                    AND COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(`Currency`, '$.Meso')) AS SIGNED), 0) = {player.Currency.Meso}
                """);
            if (paid != 1) {
                throw new DbUpdateConcurrencyException("The black-market player's saved currency snapshot has changed.");
            }
            return Context.Character.Where(character => character.Id == player.Character.Id)
                .Select(character => character.LastModified).Single();
        }

        private (Model.BlackMarketListing? Listing, Model.Item? Item) LockBlackMarketListing(long listingId) {
            Model.BlackMarketListing? listing = Context.BlackMarketListing
                .FromSqlInterpolated($"SELECT * FROM `black-market-listing` WHERE `Id` = {listingId} FOR UPDATE")
                .AsTracking().AsEnumerable().SingleOrDefault();
            if (listing == null) {
                return (null, null);
            }
            Model.Item? item = Context.Item
                .FromSqlInterpolated($"SELECT * FROM `item` WHERE `Id` = {listing.ItemUid} FOR UPDATE")
                .AsTracking().AsEnumerable().SingleOrDefault();
            if (item == null || item.OwnerId != listing.Id || item.Amount != listing.Quantity ||
                listing.Quantity <= 0 || listing.Price <= 0 || listing.Deposit < 0 || ToItem(item) == null) {
                return (listing, null);
            }
            return (listing, item);
        }

        private BlackMarketListing? ToBlackMarketingListing(Model.BlackMarketListing? model) {
            if (model == null) {
                return null;
            }
            Model.Item? itemModel = Context.Item.Find(model.ItemUid);
            if (itemModel == null || itemModel.OwnerId != model.Id || itemModel.Amount != model.Quantity || model.Quantity <= 0) {
                return null;
            }
            Item? item = ToItem(itemModel);
            return item == null ? null : model.Convert(item);
        }

        private Mail BlackMarketBuyerMail(BlackMarketListing listing, long buyerId, int quantity, long total) {
            var mail = new Mail(game.serverTableMetadata.ConstantsTable.MailExpiryDays) {
                ReceiverId = buyerId,
                Type = MailType.BlackMarketSale,
                TitleArgs = [("item", $"{listing.Item.Id}")],
                ContentArgs = [
                    ("item", $"{listing.Item.Id}"),
                    ("str", $"{quantity}"),
                    ("money", $"{total}"),
                    ("money", $"{listing.Price}"),
                ],
            };
            mail.SetTitle(StringCode.s_blackmarket_mail_to_buyer_title);
            mail.SetContent(StringCode.s_blackmarket_mail_to_buyer_content);
            mail.SetSenderName(StringCode.s_blackmarket_mail_to_sender);
            return mail;
        }

        private Mail BlackMarketSellerMail(BlackMarketListing listing, int quantity, long total, float costRate,
                                          long tax, long fee, long revenue, bool premium) {
            List<(string, string)> args = [
                ("item", $"{listing.Item.Id}"),
                ("str", $"{quantity}"),
                ("money", $"{total}"),
                ("money", $"{listing.Price}"),
                ("money", $"{tax}"),
                ("str", $"{costRate * 100}%"),
            ];
            if (listing.Quantity == 0) {
                args.Add(("money", $"{listing.Deposit}"));
            }
            args.Add(("money", $"{revenue}"));
            if (premium) {
                args.Add(("str", $"{Constant.BlackMarketPremiumClubDiscount * 100}%"));
                args.Add(("money", $"{fee}"));
            }
            var mail = new Mail(game.serverTableMetadata.ConstantsTable.MailExpiryDays) {
                ReceiverId = listing.CharacterId,
                Type = MailType.BlackMarketSale,
                TitleArgs = [("item", $"{listing.Item.Id}")],
                ContentArgs = args,
                Meso = revenue,
            };
            mail.SetContent(listing.Quantity == 0
                ? premium ? StringCode.s_blackmarket_mail_to_vipseller_content_soldout : StringCode.s_blackmarket_mail_to_seller_content_soldout
                : premium ? StringCode.s_blackmarket_mail_to_vipseller_content : StringCode.s_blackmarket_mail_to_seller_content);
            mail.SetTitle(StringCode.s_blackmarket_mail_to_seller_title);
            mail.SetSenderName(StringCode.s_blackmarket_mail_to_sender);
            return mail;
        }
    }
}
