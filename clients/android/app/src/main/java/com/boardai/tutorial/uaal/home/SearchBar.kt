package com.boardai.tutorial.uaal.home

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.text.KeyboardOptions

@Composable
fun GameSearchBar(
    query: String,
    onQueryChange: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    OutlinedTextField(
        value = query,
        onValueChange = onQueryChange,
        modifier = modifier.fillMaxWidth(),
        singleLine = true,
        placeholder = {
            Text(
                text = "搜索游戏名 / 别名 / 拼音首字母",
                color = Color(0xFF7F8999)
            )
        },
        leadingIcon = {
            Text(text = "🔍", color = Color(0xFF9AA4B2))
        },
        trailingIcon = if (query.isNotBlank()) {
            {
                TextButton(onClick = { onQueryChange("") }) {
                    Text(text = "清除", color = Color(0xFF8AB4F8))
                }
            }
        } else {
            null
        },
        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search)
    )
}
