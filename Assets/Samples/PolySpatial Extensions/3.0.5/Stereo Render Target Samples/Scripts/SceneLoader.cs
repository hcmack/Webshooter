using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Unity.PolySpatial.Samples
{
    public class SceneLoader : MonoBehaviour
    {
        [SerializeField]
        Button m_FirstButton;

        [SerializeField]
        List<string> m_LevelNames;

        void Awake()
        {
            m_FirstButton.GetComponentInChildren<TextMeshProUGUI>().text = m_LevelNames[0];
            for (int i = 1; i < m_LevelNames.Count; i++)
            {
                var levelName = m_LevelNames[i];
                var buttonInstance = Instantiate(m_FirstButton, m_FirstButton.transform.parent);
                buttonInstance.GetComponentInChildren<TextMeshProUGUI>().text = levelName;
                buttonInstance.onClick.AddListener(() => { SceneManager.LoadScene(levelName);});
            }
            m_FirstButton.onClick.AddListener(() => { SceneManager.LoadScene(m_LevelNames[0]);});
        }
    }
}
