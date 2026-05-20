#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) struct AuthoredActionId(String);

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub(crate) struct RuntimeActionId(String);

impl AuthoredActionId {
    pub(crate) fn new(value: &str) -> Self {
        Self(normalize_authored_action_id(value))
    }

    pub(crate) fn as_str(&self) -> &str {
        self.0.as_str()
    }

    pub(crate) fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    pub(crate) fn into_string(self) -> String {
        self.0
    }
}

impl RuntimeActionId {
    pub(crate) fn new(value: &str) -> Self {
        Self(normalize_runtime_action_id(value))
    }

    pub(crate) fn as_str(&self) -> &str {
        self.0.as_str()
    }

    pub(crate) fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    pub(crate) fn into_string(self) -> String {
        self.0
    }
}

pub(crate) fn normalize_authored_action_id(value: &str) -> String {
    value.trim().to_ascii_uppercase().replace('-', "_")
}

pub(crate) fn normalize_runtime_action_id(value: &str) -> String {
    value.trim().to_ascii_lowercase()
}

#[cfg(test)]
mod tests {
    use super::{AuthoredActionId, RuntimeActionId};

    #[test]
    fn authored_action_ids_normalize_to_uppercase_underscore_wire_ids() {
        let id = AuthoredActionId::new("  momentum-strike ");

        assert_eq!(id.as_str(), "MOMENTUM_STRIKE");
        assert!(!id.is_empty());
        assert_eq!(id.into_string(), "MOMENTUM_STRIKE");
    }

    #[test]
    fn runtime_action_ids_normalize_to_lowercase_runtime_ids() {
        let id = RuntimeActionId::new("  Utility_1 ");

        assert_eq!(id.as_str(), "utility_1");
        assert!(!id.is_empty());
        assert_eq!(id.into_string(), "utility_1");
    }

    #[test]
    fn empty_action_ids_remain_empty_after_normalization() {
        assert!(AuthoredActionId::new("  ").is_empty());
        assert!(RuntimeActionId::new("  ").is_empty());
    }

    #[test]
    fn authored_and_runtime_ids_do_not_implicitly_equate() {
        let authored = AuthoredActionId::new("utility_1");
        let runtime = RuntimeActionId::new("utility_1");

        assert_eq!(authored.as_str(), "UTILITY_1");
        assert_eq!(runtime.as_str(), "utility_1");
    }
}
