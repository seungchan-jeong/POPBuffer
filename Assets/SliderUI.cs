using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SliderUI : MonoBehaviour
{
    public Slider slider;
    public TMP_Text minText;
    public TMP_Text maxText;
    public TMP_Text currentText;

    public void SetMinMax(int min, int max)
    {
        slider.minValue = (float)min;
        slider.maxValue = (float)max;

        minText.text = min.ToString();
        maxText.text = max.ToString();
    }

    public void SetCurrentValue(int value)
    {
        slider.SetValueWithoutNotify((float)value);
        currentText.text = value.ToString();
    }
}
