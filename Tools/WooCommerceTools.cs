using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using WordpressMCPSharp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WordpressMCPSharp.Tools;

/// <summary>
/// WooCommerce store management over the wc/v3 REST namespace. Authenticates with the endpoint's
/// application password; stores that require key/secret auth can set WooCommerceConsumerKey/Secret
/// on the endpoint's RestApi block.
/// </summary>
[McpServerToolType]
public static class WooCommerceTools
{
    [McpServerTool(Name = "wp_wc_list_products"),
     Description("List WooCommerce products with optional filters. Returns slim product data plus pagination totals.")]
    public static async Task<string> ListProducts(
        EndpointRegistry registry,
        [Description("Free-text search across product names and descriptions.")] string? search = null,
        [Description("Filter: status — any, draft, pending, private, publish.")] string? status = null,
        [Description("Filter: product category id.")] int? categoryId = null,
        [Description("Filter: stock status — instock, outofstock, onbackorder.")] string? stockStatus = null,
        [Description("Filter: product type — simple, grouped, external, variable.")] string? type = null,
        [Description("Order by: date, id, title, slug, price, popularity, rating.")] string? orderby = null,
        [Description("Order direction: asc or desc.")] string? order = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_list_products", ct);
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (categoryId.HasValue) qs.Add($"category={categoryId.Value}");
        if (!string.IsNullOrWhiteSpace(stockStatus)) qs.Add($"stock_status={Uri.EscapeDataString(stockStatus)}");
        if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(orderby)) qs.Add($"orderby={Uri.EscapeDataString(orderby)}");
        if (!string.IsNullOrWhiteSpace(order)) qs.Add($"order={Uri.EscapeDataString(order)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wc/v3/products?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(SummarizeProduct);
        return WpUtil.PagedResult(total, totalPages, page, items);
    }

    [McpServerTool(Name = "wp_wc_get_product"),
     Description("Get full detail for one WooCommerce product, including pricing, stock, dimensions, attributes and images.")]
    public static async Task<string> GetProduct(
        EndpointRegistry registry,
        [Description("Product id.")] int id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_get_product", ct);
        var node = await svc.GetJsonAsync($"wp-json/wc/v3/products/{id}", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_wc_create_product"),
     Description("Create a WooCommerce product. Requires write mode.")]
    public static async Task<string> CreateProduct(
        EndpointRegistry registry,
        [Description("Product name.")] string name,
        [Description("Regular price as a decimal string, e.g. `19.99`.")] string? regularPrice = null,
        [Description("Product type: simple (default), grouped, external, variable.")] string type = "simple",
        [Description("Status: draft (default), pending, private, publish.")] string status = "draft",
        [Description("Full product description (HTML allowed).")] string? description = null,
        [Description("Short description shown near the price.")] string? shortDescription = null,
        [Description("Stock keeping unit.")] string? sku = null,
        [Description("Sale price as a decimal string.")] string? salePrice = null,
        [Description("Set true to track stock quantity for this product.")] bool manageStock = false,
        [Description("Stock quantity, used when manageStock is true.")] int? stockQuantity = null,
        [Description("Product category ids as a JSON array, e.g. `[15,22]`.")] string? categoryIdsJson = null,
        [Description("Media (attachment) ids to use as product images, as a JSON array. The first is the main image.")] string? imageIdsJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_create_product", ct);
        svc.EnsureWriteAllowed("wp_wc_create_product");

        var body = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["status"] = status,
        };
        if (regularPrice is not null) body["regular_price"] = regularPrice;
        if (salePrice is not null) body["sale_price"] = salePrice;
        if (description is not null) body["description"] = description;
        if (shortDescription is not null) body["short_description"] = shortDescription;
        if (sku is not null) body["sku"] = sku;
        if (manageStock)
        {
            body["manage_stock"] = true;
            if (stockQuantity.HasValue) body["stock_quantity"] = stockQuantity.Value;
        }
        if (categoryIdsJson is not null) body["categories"] = IdObjects(categoryIdsJson, nameof(categoryIdsJson));
        if (imageIdsJson is not null) body["images"] = IdObjects(imageIdsJson, nameof(imageIdsJson));

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wc/v3/products", body, ct);
        return JsonSerializer.Serialize(SummarizeProduct(result), JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_wc_update_product"),
     Description("Update a WooCommerce product's fields (PATCH semantics — only supplied fields change). Requires write mode.")]
    public static async Task<string> UpdateProduct(
        EndpointRegistry registry,
        [Description("Product id.")] int id,
        [Description("New name.")] string? name = null,
        [Description("New regular price as a decimal string.")] string? regularPrice = null,
        [Description("New sale price as a decimal string. Pass an empty string to clear it.")] string? salePrice = null,
        [Description("New status: draft, pending, private, publish.")] string? status = null,
        [Description("New description.")] string? description = null,
        [Description("New short description.")] string? shortDescription = null,
        [Description("New SKU.")] string? sku = null,
        [Description("New stock status: instock, outofstock, onbackorder.")] string? stockStatus = null,
        [Description("New stock quantity (implies stock management).")] int? stockQuantity = null,
        [Description("Replacement category ids as a JSON array.")] string? categoryIdsJson = null,
        [Description("Replacement image media ids as a JSON array.")] string? imageIdsJson = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_update_product", ct);
        svc.EnsureWriteAllowed("wp_wc_update_product");

        var body = new JsonObject();
        if (name is not null) body["name"] = name;
        if (regularPrice is not null) body["regular_price"] = regularPrice;
        if (salePrice is not null) body["sale_price"] = salePrice;
        if (status is not null) body["status"] = status;
        if (description is not null) body["description"] = description;
        if (shortDescription is not null) body["short_description"] = shortDescription;
        if (sku is not null) body["sku"] = sku;
        if (stockStatus is not null) body["stock_status"] = stockStatus;
        if (stockQuantity.HasValue)
        {
            body["manage_stock"] = true;
            body["stock_quantity"] = stockQuantity.Value;
        }
        if (categoryIdsJson is not null) body["categories"] = IdObjects(categoryIdsJson, nameof(categoryIdsJson));
        if (imageIdsJson is not null) body["images"] = IdObjects(imageIdsJson, nameof(imageIdsJson));

        if (body.Count == 0)
            throw new McpException("wp_wc_update_product: at least one field must be supplied.");

        var result = await svc.SendJsonAsync(HttpMethod.Put, $"wp-json/wc/v3/products/{id}", body, ct);
        return JsonSerializer.Serialize(SummarizeProduct(result), JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_wc_delete_product"),
     Description("Move a product to trash, or delete it permanently with force=true. Permanent deletion requires Wordpress:AllowDelete=true.")]
    public static async Task<string> DeleteProduct(
        EndpointRegistry registry,
        [Description("Product id.")] int id,
        [Description("If true, bypass trash and delete permanently.")] bool force = false,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_delete_product", ct);
        if (force) svc.EnsureDeleteAllowed("wp_wc_delete_product");
        else svc.EnsureWriteAllowed("wp_wc_delete_product");

        await svc.SendJsonAsync(HttpMethod.Delete, $"wp-json/wc/v3/products/{id}?force={(force ? "true" : "false")}", null, ct);
        return JsonSerializer.Serialize(new { id, trashed = !force, deleted = force }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_wc_list_orders"),
     Description("List WooCommerce orders with optional filters. Returns slim order data plus pagination totals.")]
    public static async Task<string> ListOrders(
        EndpointRegistry registry,
        [Description("Filter: status — any, pending, processing, on-hold, completed, cancelled, refunded, failed.")] string? status = null,
        [Description("Filter: customer user id.")] int? customerId = null,
        [Description("Free-text search.")] string? search = null,
        [Description("Only orders created after this ISO 8601 date-time.")] string? after = null,
        [Description("Only orders created before this ISO 8601 date-time.")] string? before = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_list_orders", ct);
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (customerId.HasValue) qs.Add($"customer={customerId.Value}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(after)) qs.Add($"after={Uri.EscapeDataString(after)}");
        if (!string.IsNullOrWhiteSpace(before)) qs.Add($"before={Uri.EscapeDataString(before)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wc/v3/orders?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(o => o is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            number = d["number"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            total = d["total"]?.GetValue<string?>(),
            currency = d["currency"]?.GetValue<string?>(),
            dateCreated = d["date_created"]?.GetValue<string?>(),
            datePaid = d["date_paid"]?.GetValue<string?>(),
            customerId = d["customer_id"]?.GetValue<int?>(),
            customerName = $"{(d["billing"] as JsonObject)?["first_name"]?.GetValue<string?>()} {(d["billing"] as JsonObject)?["last_name"]?.GetValue<string?>()}".Trim(),
            email = (d["billing"] as JsonObject)?["email"]?.GetValue<string?>(),
            paymentMethod = d["payment_method_title"]?.GetValue<string?>(),
            itemCount = (d["line_items"] as JsonArray)?.Count,
        } : (object?)o);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_wc_get_order"),
     Description("Get full detail for one order, including line items, billing/shipping addresses, totals and taxes.")]
    public static async Task<string> GetOrder(
        EndpointRegistry registry,
        [Description("Order id.")] int id,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_get_order", ct);
        var node = await svc.GetJsonAsync($"wp-json/wc/v3/orders/{id}", ct);
        if (node is JsonObject obj) obj.Remove("_links");
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_wc_update_order"),
     Description("Update an order's status or customer note (for example marking it completed). Requires write mode.")]
    public static async Task<string> UpdateOrder(
        EndpointRegistry registry,
        [Description("Order id.")] int id,
        [Description("New status: pending, processing, on-hold, completed, cancelled, refunded, failed.")] string? status = null,
        [Description("Customer-visible note attached to the order.")] string? customerNote = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_update_order", ct);
        svc.EnsureWriteAllowed("wp_wc_update_order");

        var body = new JsonObject();
        if (status is not null) body["status"] = status;
        if (customerNote is not null) body["customer_note"] = customerNote;
        if (body.Count == 0)
            throw new McpException("wp_wc_update_order: supply status or customerNote.");

        var result = await svc.SendJsonAsync(HttpMethod.Put, $"wp-json/wc/v3/orders/{id}", body, ct);
        return JsonSerializer.Serialize(new
        {
            id = result?["id"]?.GetValue<int?>(),
            status = result?["status"]?.GetValue<string?>(),
            total = result?["total"]?.GetValue<string?>(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "wp_wc_list_customers"),
     Description("List WooCommerce customers with their order counts and total spend.")]
    public static async Task<string> ListCustomers(
        EndpointRegistry registry,
        [Description("Free-text search (name or email).")] string? search = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_list_customers", ct);
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wc/v3/customers?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(c => c is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            email = d["email"]?.GetValue<string?>(),
            firstName = d["first_name"]?.GetValue<string?>(),
            lastName = d["last_name"]?.GetValue<string?>(),
            username = d["username"]?.GetValue<string?>(),
            ordersCount = d["orders_count"]?.GetValue<int?>(),
            totalSpent = d["total_spent"]?.GetValue<string?>(),
            dateCreated = d["date_created"]?.GetValue<string?>(),
        } : (object?)c);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_wc_list_product_categories"),
     Description("List WooCommerce product categories with product counts.")]
    public static async Task<string> ListProductCategories(
        EndpointRegistry registry,
        [Description("Free-text search.")] string? search = null,
        [Description("Page number (1-based). Defaults to 1.")] int page = 1,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_list_product_categories", ct);
        var qs = new List<string> { $"page={Math.Max(1, page)}", $"per_page={svc.Options.DefaultPageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");

        var (node, total, totalPages) = await svc.GetJsonPagedAsync("wp-json/wc/v3/products/categories?" + string.Join('&', qs), ct);
        var items = (node as JsonArray ?? new JsonArray()).Select(c => c is JsonObject d ? new
        {
            id = d["id"]?.GetValue<int?>(),
            name = d["name"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            parent = d["parent"]?.GetValue<int?>(),
            count = d["count"]?.GetValue<int?>(),
            description = d["description"]?.GetValue<string?>(),
        } : (object?)c);
        return WpUtil.PagedResult(total, totalPages, page, items!);
    }

    [McpServerTool(Name = "wp_wc_create_product_category"),
     Description("Create a WooCommerce product category. Requires write mode.")]
    public static async Task<string> CreateProductCategory(
        EndpointRegistry registry,
        [Description("Category name.")] string name,
        [Description("Optional URL slug.")] string? slug = null,
        [Description("Optional parent category id.")] int? parentId = null,
        [Description("Optional description.")] string? description = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_create_product_category", ct);
        svc.EnsureWriteAllowed("wp_wc_create_product_category");

        var body = new JsonObject { ["name"] = name };
        if (slug is not null) body["slug"] = slug;
        if (parentId.HasValue) body["parent"] = parentId.Value;
        if (description is not null) body["description"] = description;

        var result = await svc.SendJsonAsync(HttpMethod.Post, "wp-json/wc/v3/products/categories", body, ct);
        if (result is JsonObject obj) obj.Remove("_links");
        return result?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_wc_get_reports"),
     Description("Get WooCommerce sales reports: totals for a period, or the top sellers. Useful for a quick view of how a store is performing.")]
    public static async Task<string> GetReports(
        EndpointRegistry registry,
        [Description("Report type: sales (default) or top_sellers.")] string report = "sales",
        [Description("Period: week, month, last_month, year. Ignored when explicit dates are supplied.")] string? period = null,
        [Description("Start date (YYYY-MM-DD), used with dateMax.")] string? dateMin = null,
        [Description("End date (YYYY-MM-DD), used with dateMin.")] string? dateMax = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_get_reports", ct);
        var path = report.ToLowerInvariant() switch
        {
            "sales" => "sales",
            "top_sellers" or "top-sellers" => "top_sellers",
            _ => throw new McpException("report must be 'sales' or 'top_sellers'."),
        };

        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(dateMin) && !string.IsNullOrWhiteSpace(dateMax))
        {
            qs.Add($"date_min={Uri.EscapeDataString(dateMin)}");
            qs.Add($"date_max={Uri.EscapeDataString(dateMax)}");
        }
        else if (!string.IsNullOrWhiteSpace(period))
        {
            qs.Add($"period={Uri.EscapeDataString(period)}");
        }

        var url = $"wp-json/wc/v3/reports/{path}" + (qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty);
        var node = await svc.GetJsonAsync(url, ct);
        return node?.ToJsonString(JsonOpts.Default) ?? "null";
    }

    [McpServerTool(Name = "wp_wc_get_settings"),
     Description("Get WooCommerce store settings for a group (general, products, tax, shipping, checkout, account, email). Omit the group to list the available groups.")]
    public static async Task<string> GetSettings(
        EndpointRegistry registry,
        [Description("Settings group id, e.g. `general`. Omit to list groups.")] string? group = null,
        [Description("Endpoint name from wp_list_endpoints. Omit to use the default site.")] string? site = null,
        CancellationToken ct = default)
    {
        var svc = await RequireWooAsync(registry, site, "wp_wc_get_settings", ct);
        var url = string.IsNullOrWhiteSpace(group)
            ? "wp-json/wc/v3/settings"
            : $"wp-json/wc/v3/settings/{Uri.EscapeDataString(group)}";
        var node = await svc.GetJsonAsync(url, ct);

        if (node is not JsonArray arr) return node?.ToJsonString(JsonOpts.Default) ?? "null";
        var items = arr.Select(s => s is JsonObject d ? new
        {
            id = d["id"]?.GetValue<string?>(),
            label = d["label"]?.GetValue<string?>(),
            description = d["description"]?.GetValue<string?>(),
            value = d["value"]?.ToJsonString(),
            @default = d["default"]?.ToJsonString(),
            type = d["type"]?.GetValue<string?>(),
        } : (object?)s);
        return JsonSerializer.Serialize(items, JsonOpts.Default);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Resolve the endpoint and confirm WooCommerce is actually available before calling wc/v3.</summary>
    private static async Task<WordpressRestClient> RequireWooAsync(EndpointRegistry registry, string? site, string operation, CancellationToken ct)
    {
        var svc = registry.RequireRest(site, operation);
        svc.EnsureFeature(svc.Options.EnableWooCommerce, "WooCommerce");

        if (!await svc.HasNamespaceAsync("wc/v3", ct))
        {
            throw new McpException(
                $"MCP tool '{operation}' requires WooCommerce, but the wc/v3 REST namespace is not available on endpoint '{svc.EndpointName}'. " +
                "Install and activate WooCommerce on the site (wp_install_plugin with slug 'woocommerce'), then retry.");
        }

        return svc;
    }

    private static JsonArray IdObjects(string json, string paramName)
    {
        var ids = WpUtil.ParseIntArray(json, paramName);
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(new JsonObject { ["id"] = id!.GetValue<int>() });
        }
        return array;
    }

    private static object SummarizeProduct(JsonNode? node)
    {
        if (node is not JsonObject d) return node!;
        return new
        {
            id = d["id"]?.GetValue<int?>(),
            name = d["name"]?.GetValue<string?>(),
            slug = d["slug"]?.GetValue<string?>(),
            status = d["status"]?.GetValue<string?>(),
            type = d["type"]?.GetValue<string?>(),
            sku = d["sku"]?.GetValue<string?>(),
            price = d["price"]?.GetValue<string?>(),
            regularPrice = d["regular_price"]?.GetValue<string?>(),
            salePrice = d["sale_price"]?.GetValue<string?>(),
            onSale = d["on_sale"]?.GetValue<bool?>(),
            stockStatus = d["stock_status"]?.GetValue<string?>(),
            stockQuantity = d["stock_quantity"]?.GetValue<int?>(),
            totalSales = d["total_sales"]?.GetValue<int?>(),
            permalink = d["permalink"]?.GetValue<string?>(),
            categories = (d["categories"] as JsonArray)?.Select(c => (c as JsonObject)?["name"]?.GetValue<string?>()),
        };
    }
}
