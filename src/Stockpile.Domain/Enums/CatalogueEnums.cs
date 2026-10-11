namespace Stockpile.Domain.Enums;

/// <summary>Product line kind (domain-model §4.3).</summary>
public enum ItemType
{
    PhysicalProduct = 0,
    Service = 1,
}

/// <summary>How a product's price is exposed (domain-model §4.4).</summary>
public enum PriceState
{
    Public = 0,
    OnRequest = 1,
    BulkTiered = 2,
    Negotiable = 3,
}

/// <summary>Supplier-declared stock availability (domain-model §4.5).</summary>
public enum StockState
{
    InStock = 0,
    LowStock = 1,
    OutOfStock = 2,
    MadeToOrder = 3,
    EnquireForAvailability = 4,
}

/// <summary>Attribute value type (domain-model §4.2 hybrid model).</summary>
public enum AttributeDataType
{
    Text = 0,
    LongText = 1,
    Number = 2,
    Boolean = 3,
    Enum = 4,
    MultiSelect = 5,
    Date = 6,
}

/// <summary>UOM dimension family (domain-model §4.6).</summary>
public enum DimensionKind
{
    Count = 0,
    Length = 1,
    Area = 2,
    Volume = 3,
    Mass = 4,
    Time = 5,
}

/// <summary>Supplier verification state (domain-model §3.1), orthogonal to status.</summary>
public enum VerificationState
{
    Unverified = 0,
    Pending = 1,
    Verified = 2,
    Rejected = 3,
    Expired = 4,
}

/// <summary>Default audience of a supplier's public page (domain-model §3.4).</summary>
public enum SupplierAudience
{
    Contractors = 0,
    Architects = 1,
    Consultants = 2,
    Developers = 3,
    Everyone = 4,
}

/// <summary>Scope of an audience policy (domain-model §3.4).</summary>
public enum AudienceAppliesTo
{
    AllProducts = 0,
    SelectedProducts = 1,
}

/// <summary>Details a supplier exposes to its audience (domain-model §3.4).</summary>
public enum SupplierVisibility
{
    Prices = 0,
    FullDetails = 1,
}

/// <summary>How strongly a supplier serves a category (domain-model §3.3).</summary>
public enum SpecialtyLevel
{
    Primary = 0,
    Secondary = 1,
}

/// <summary>Verification request outcome (domain-model §3.2).</summary>
public enum VerificationDecision
{
    None = 0,
    Approved = 1,
    Rejected = 2,
}