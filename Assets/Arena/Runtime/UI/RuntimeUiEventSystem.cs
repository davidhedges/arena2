#nullable enable

using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Arena.UI
{
    public static class RuntimeUiEventSystem
    {
        public static void Ensure()
        {
            EventSystem[] all = UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include);
            EventSystem? eventSystem = all.Length > 0 ? all[0] : null;
            for (int i = 1; i < all.Length; i++)
                UnityEngine.Object.Destroy(all[i].gameObject);

            if (eventSystem == null)
            {
                GameObject go = new("RuntimeEventSystem");
                UnityEngine.Object.DontDestroyOnLoad(go);
                eventSystem = go.AddComponent<EventSystem>();
            }

            if (!eventSystem.isActiveAndEnabled)
                eventSystem.gameObject.SetActive(true);

            InputSystemUIInputModule? inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
            if (inputModule == null)
                inputModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();

            foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>())
            {
                bool shouldEnable = ReferenceEquals(module, inputModule);
                if (module.enabled != shouldEnable)
                    module.enabled = shouldEnable;
            }
        }
    }
}
